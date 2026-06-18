using System.Text.Json;
using Ada.Core;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Agents.AI;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Configuration.Memory;
using Microsoft.Extensions.DependencyInjection;
using Voxa.AspNetCore;
using Voxa.Audio.SileroVad;
using Voxa.Speech.Kokoro;
using Voxa.Speech.Piper;
using Voxa.Speech.WhisperCpp;

namespace Ada.Voice;

/// <summary>Body for <c>POST /api/voice</c> — change the audio pipeline from Settings → Voice (all optional).</summary>
public sealed record VoiceSettingsDto(string? SttModel = null, string? SttLanguage = null, string? TtsProvider = null, string? TtsVoice = null);

/// <summary>
/// Ada's voice plane (M6): hosts the Voxa pipeline in-process on the loopback server, fully local —
/// Silero VAD, WhisperCpp STT, and a choice of Piper or Kokoro TTS. The STT model, TTS engine and voice
/// are read from <see cref="AdaConfig"/> per connection, so the user can reconfigure them live in
/// Settings → Voice. The agent is the same Ada the text surface uses; barge-in is handled by Voxa.
/// </summary>
public static class AdaVoice
{
    /// <summary>Registers Voxa with Ada's local speech stack (both TTS engines). Models download on use.</summary>
    public static void AddAdaVoice(WebApplicationBuilder builder)
    {
        var cfg = new ConfigStore().Load();
        // Lowest-priority defaults seeded from the user's saved choice; real config/env still wins. The
        // per-connection pipeline (below) is what actually selects the model/voice at request time.
        var defaults = new Dictionary<string, string?>
        {
            ["Voxa:Profile"] = "LowLatency",
            ["Voxa:Stt"] = "WhisperCpp",
            ["Voxa:Tts"] = cfg.TtsProvider,
            ["Voxa:WhisperCpp:Model"] = cfg.SttModel,
            ["Voxa:WhisperCpp:Language"] = cfg.SttLanguage,
            ["Voxa:Piper:Voice"] = cfg.TtsProvider == "Kokoro" ? "en_US-lessac-medium" : cfg.TtsVoice,
            ["Voxa:Kokoro:Voice"] = cfg.TtsProvider == "Kokoro" ? cfg.TtsVoice : "af_heart",
            ["Voxa:Vad:Engine"] = "Silero",
            ["Voxa:Models:EagerWarmup"] = "false", // we warm explicitly via Settings → Voice
            ["Voxa:Agent:Provider"] = "Echo",      // Ada supplies her own agent; keep Voxa's keyless
        };
        builder.Configuration.Sources.Insert(0, new MemoryConfigurationSource { InitialData = defaults });

        builder.Services.AddVoxa(builder.Configuration, voxa =>
        {
            voxa.AddProvider(WhisperCppDescriptors.Stt);
            voxa.AddProvider(PiperDescriptors.Tts);
            voxa.AddProvider(KokoroDescriptors.Tts);
            voxa.AddProvider(SileroVadDescriptors.Vad);
        });
    }

    /// <summary>Maps the loopback voice endpoint and composes the local pipeline around Ada's agent.</summary>
    public static void MapAdaVoice(WebApplication app)
    {
        app.UseWebSockets();
        app.MapVoxaVoice("/voice").Use((context, pipeline) =>
        {
            var agent = context.RequestServices.GetRequiredService<AIAgent>();
            var cfg = new ConfigStore().Load();                 // current selection, per connection
            var voxaCfg = BuildVoxaSection(cfg);
            var sp = context.RequestServices;
            var tts = string.Equals(cfg.TtsProvider, "Kokoro", StringComparison.OrdinalIgnoreCase)
                ? KokoroDescriptors.Tts : PiperDescriptors.Tts;

            pipeline
                .UseSilenceGate()
                .UseSpeechToText(() => WhisperCppDescriptors.Stt.CreateProcessor(sp, voxaCfg))
                .UseTranscriptionFilter()
                .UseProcessor(() => new BlankTranscriptionFilter())   // drop [BLANK_AUDIO] etc. before the agent
                .UseMicrosoftAgent(agent)
                .UseSentenceAggregator()
                .UseTextToSpeech(() => tts.CreateProcessor(sp, voxaCfg));
        });

        // Voice config + status for Settings → Voice. Mapped only when voice is enabled (404 ⇒ off).
        app.MapGet("/api/voice", () =>
        {
            var cfg = new ConfigStore().Load();
            var models = VoiceModels.Status(cfg.SttModel, cfg.TtsProvider, cfg.TtsVoice);
            var ready = true;
            foreach (var m in models) if (!m.Ready) { ready = false; break; }
            return Results.Json(new
            {
                enabled = true,
                profile = "LowLatency",
                vad = "Silero",
                inputRate = 16000,
                outputRate = VoiceModels.OutputRateFor(cfg.TtsProvider, cfg.TtsVoice),
                stt = new { engine = "WhisperCpp", model = cfg.SttModel, language = cfg.SttLanguage, options = VoiceModels.SttModels() },
                tts = new { provider = cfg.TtsProvider, voice = cfg.TtsVoice, providers = new[] { "Piper", "Kokoro" }, voices = VoiceModels.TtsVoices() },
                models,
                ready,
            });
        });

        // Change the pipeline (STT model / TTS engine / voice). Takes effect on the next voice session.
        app.MapPost("/api/voice", (VoiceSettingsDto dto) =>
        {
            var store = new ConfigStore();
            var cfg = store.Load();
            if (!string.IsNullOrWhiteSpace(dto.SttModel)) cfg.SttModel = dto.SttModel;
            if (!string.IsNullOrWhiteSpace(dto.SttLanguage)) cfg.SttLanguage = dto.SttLanguage;
            if (!string.IsNullOrWhiteSpace(dto.TtsProvider)) cfg.TtsProvider = dto.TtsProvider;
            if (!string.IsNullOrWhiteSpace(dto.TtsVoice)) cfg.TtsVoice = dto.TtsVoice;
            store.Save(cfg);
            return Results.Json(new { outputRate = VoiceModels.OutputRateFor(cfg.TtsProvider, cfg.TtsVoice) });
        });

        app.MapPost("/api/voice/warmup", async (HttpContext http, CancellationToken ct) =>
        {
            var cfg = new ConfigStore().Load();
            http.Response.Headers.ContentType = "text/event-stream";
            var progress = new Progress<string>(s =>
                _ = http.Response.WriteAsync($"event: progress\ndata: {JsonSerializer.Serialize(s)}\n\n", ct));
            try
            {
                await VoiceModels.WarmUpAsync(cfg.SttModel, cfg.TtsProvider, cfg.TtsVoice, progress, ct);
                await http.Response.WriteAsync("event: done\ndata: {}\n\n", ct);
            }
            catch (Exception ex)
            {
                await http.Response.WriteAsync($"event: error\ndata: {JsonSerializer.Serialize(ex.Message)}\n\n", ct);
            }
        });
    }

    /// <summary>Build a per-connection <c>"Voxa"</c> section carrying the user's STT model + TTS voice.</summary>
    private static IConfigurationSection BuildVoxaSection(AdaConfig cfg)
    {
        var kokoro = string.Equals(cfg.TtsProvider, "Kokoro", StringComparison.OrdinalIgnoreCase);
        var kv = new Dictionary<string, string?>
        {
            ["Voxa:Profile"] = "LowLatency",
            ["Voxa:WhisperCpp:Model"] = cfg.SttModel,
            ["Voxa:WhisperCpp:Language"] = cfg.SttLanguage,
            ["Voxa:Piper:Voice"] = kokoro ? null : cfg.TtsVoice,
            ["Voxa:Kokoro:Voice"] = kokoro ? cfg.TtsVoice : null,
            ["Voxa:Vad:Engine"] = "Silero",
        };
        return new ConfigurationBuilder().AddInMemoryCollection(kv).Build().GetSection("Voxa");
    }
}

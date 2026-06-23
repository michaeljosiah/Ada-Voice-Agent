using System.Text.Json;
using Ada.Core;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Configuration.Memory;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;   // IOptions<VoxaOptions>
using Voxa.AspNetCore;
using Voxa.Audio.SileroVad;
using Voxa.Processors;
using Voxa.Speech;                     // VoxaVadSettings
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
    /// <summary>The rate the browser client captures and downsamples to (wwwroot <c>VOICE_IN_RATE</c>);
    /// Whisper's native rate. Inbound frames must carry this before the Silero VAD, which forwards
    /// audio ungated when the frame rate ≠ its configured rate.</summary>
    private const int ClientInputSampleRate = 16000;

    /// <summary>Hold the mic open long enough for natural pauses; Voxa LowLatency's 400 ms is too eager for Ada.</summary>
    private static readonly TimeSpan VoiceStopDuration = TimeSpan.FromMilliseconds(900);

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
            var engine = context.RequestServices.GetRequiredService<IAdaEngine>();
            var convos = context.RequestServices.GetService<IConversationStore>();
            var cfg = new ConfigStore().Load();                 // current selection, per connection
            var voxaCfg = BuildVoxaSection(cfg);
            var sp = context.RequestServices;
            var vlog = sp.GetService<ILoggerFactory>()?.CreateLogger("Ada.Voice.Turn");
            var tts = string.Equals(cfg.TtsProvider, "Kokoro", StringComparison.OrdinalIgnoreCase)
                ? KokoroDescriptors.Tts : PiperDescriptors.Tts;

            // Persist the voice conversation into a thread so Ada remembers within the session and the
            // history shows alongside text. The in-chat mic passes ?thread=<id> to continue the active
            // text thread; otherwise voice gets its own thread, created lazily on the first turn.
            var threadId = context.Request.Query["thread"].FirstOrDefault();
            if (string.IsNullOrWhiteSpace(threadId) || convos?.Load(threadId) is null) threadId = null;

            // Honour the configured Voxa:Profile (LowLatency) on this hand-built chain. Voxa's composer does
            // this for us under .UseDefaults(); we resolve the same tuning so the manual route stays faithful
            // to whatever Voxa:Profile is set — no magic constants in Ada.
            var tuning = sp.GetRequiredService<VoxaTuningResolver>()
                           .Resolve(sp.GetRequiredService<IOptions<VoxaOptions>>().Value);

            var vadSettings = new VoxaVadSettings(
                SampleRate:          ClientInputSampleRate,
                ConfidenceThreshold: tuning.VadConfidenceThreshold,
                MinRms:              tuning.VadMinRms,
                StartDuration:       tuning.VadStartDuration,
                StopDuration:        VoiceStopDuration,
                PrerollDuration:     tuning.VadPrerollDuration)
            {
                // Eager STT is great for demos, but without smart-turn it can split normal pauses mid-thought.
                EagerSttDelay        = null,
                MaxUtteranceDuration = tuning.VadMaxUtteranceDuration,
            };
            vlog?.LogInformation("[voice] Ada VAD tuning — stop={Stop}ms eagerStt=off maxResponse=120000ms", (int)VoiceStopDuration.TotalMilliseconds);

            string? ThreadIdForVoiceTurn(VoiceTurnContext ctx)
            {
                vlog?.LogInformation("[voice] agent INVOKED — transcript: {Text}", ctx.UserText);
                if (convos is null) return null;
                if (threadId is null)
                    threadId = convos.Create(VoiceTitle(ctx.UserText)).Id;
                return threadId;
            }

            pipeline
                .UseProcessor(() => new TranscriptTap("raw-in", vlog))   // FIRST: is mic audio arriving at all, and at what rate?
                // Tag inbound frames at their true 16 kHz, then run the real Silero VAD (the engine Ada
                // registers and downloads) instead of the fixed-constant energy gate UseSilenceGate() builds.
                .UseProcessor(() => new InputRateTagProcessor(ClientInputSampleRate))
                .UseProcessor(() => SileroVadDescriptors.Vad.CreateProcessor(sp, vadSettings))
                .UseProcessor(() => new TranscriptTap("VAD-out", vlog))   // audio rate + VAD speech-start/stop frames
                .UseSpeechToText(() => WhisperCppDescriptors.Stt.CreateProcessor(sp, voxaCfg))
                .UseProcessor(() => new TranscriptTap("STT-out", vlog))   // STT finals before any filter
                .UseTranscriptionFilter()
                .UseProcessor(() => new BlankTranscriptionFilter(vlog))   // drop [BLANK_AUDIO] etc. before the agent
                .UseProcessor(() => new AgentLoopProcessor(
                    new AdaEngineTurnDriver(engine, ThreadIdForVoiceTurn, vlog),
                    onTurnCompleted: (ctx, summary, _) =>
                    {
                        vlog?.LogInformation("[voice] turn COMPLETED — reply: {Text}", summary.AssistantText);
                        return ValueTask.CompletedTask;
                    },
                    maxResponseDuration: TimeSpan.FromSeconds(120)))
                .UseProcessor(() => new TranscriptTap("agent-out", vlog))   // what the agent actually replies (text frames)
                // Profile-tuned aggregator (LowLatency: 40-char eager first chunk / 350-char cap) that also
                // flushes on LLM turn end, because Ada keeps the voice WebSocket open across turns.
                .UseProcessor(() => new TurnEndSentenceAggregator
                {
                    EagerFirstChunkMinChars = tuning.EagerFirstChunkMinChars,
                    MaxBufferChars          = tuning.MaxBufferChars,
                })
                .UseProcessor(() => new TranscriptTap("tts-in", vlog))      // what reaches TTS after aggregation
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

        // Prelaunch readiness: are the speech models cached, AND does the agent's model actually answer?
        // The decisive check behind a silent "Hi → stuck on thinking" — also runnable via `ada doctor`.
        app.MapGet("/api/voice/preflight", async (HttpContext http, CancellationToken ct) =>
        {
            var cfg = new ConfigStore().Load();
            var report = await VoicePreflight.RunAsync(http.RequestServices, cfg.SttModel, cfg.TtsProvider, cfg.TtsVoice, ct: ct);
            return Results.Json(new
            {
                ok = report.Ok,
                worst = report.Worst.ToString().ToLowerInvariant(),
                checks = report.Checks.Select(c => new { name = c.Name, status = c.Status.ToString().ToLowerInvariant(), detail = c.Detail }),
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

    private static string VoiceTitle(string userText)
    {
        var t = (userText ?? string.Empty).Trim().ReplaceLineEndings(" ");
        if (t.Length == 0) return "Voice chat";
        return t.Length > 60 ? t[..60].TrimEnd() + "…" : t;
    }
}

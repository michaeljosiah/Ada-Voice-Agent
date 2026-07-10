using System.Text.Json;
using Ada.Core;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Configuration.Memory;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Voxa.AspNetCore;
using Voxa.Audio.SileroVad;
using Voxa.Processors;
using Voxa.Speech.Kokoro;
using Voxa.Speech.Piper;
using Voxa.Speech.WhisperCpp;

namespace Ada.Voice;

/// <summary>Body for <c>POST /api/voice</c> — change the audio pipeline from Settings → Voice (all optional).</summary>
public sealed record VoiceSettingsDto(string? SttModel = null, string? SttLanguage = null, string? TtsProvider = null, string? TtsVoice = null);

/// <summary>
/// Ada's voice plane (M6, rebuilt on Voxa's composer for M10): fully local — Silero VAD, WhisperCpp
/// STT, Piper or Kokoro TTS — via <c>MapVoxaVoice("/voice").UseDefaults()</c>. Ada registers her
/// engine as the composed pipeline's <c>IAgentTurnDriver</c> (VDX-007) and the M10 thinker as the
/// background agent (VDX-008), and keeps everything else Voxa ships: the real barge-in path,
/// bracket-marker transcript filtering (retires <c>BlankTranscriptionFilter</c>), profile tuning
/// (the 900 ms stop duration is now <c>Voxa:Vad:StopDurationMs</c>), and diagnostics taps.
/// </summary>
public static class AdaVoice
{
    /// <summary>Registers Voxa with Ada's local speech stack and Ada's turn drivers. Models download on use.</summary>
    public static void AddAdaVoice(WebApplicationBuilder builder)
    {
        var cfg = new ConfigStore().Load();
        // Lowest-priority defaults seeded from the user's saved choice; the live source below and
        // real config/env still win. Voxa:Vad:StopDurationMs carries what the hand-built chain
        // hard-coded: hold the mic open for natural pauses (LowLatency's 400 ms is too eager for Ada).
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
            ["Voxa:Vad:StopDurationMs"] = "900",
            ["Voxa:Models:EagerWarmup"] = "false", // we warm explicitly via Settings → Voice
            ["Voxa:Agent:Provider"] = "Echo",      // never used (Ada's driver replaces the stage) but keeps Voxa keyless
        };
        builder.Configuration.Sources.Insert(0, new MemoryConfigurationSource { InitialData = defaults });
        // Settings → Voice changes apply on the NEXT session: the composer reads these keys per
        // connection, so a live source over ConfigStore keeps model/voice/language switches working
        // without a restart. (Switching the TTS *provider* still needs a restart — Voxa:Tts is bound
        // once into VoxaOptions; documented Path-B refinement in docs/m10-talker-thinker-spec.html.)
        builder.Configuration.Sources.Insert(1, new AdaVoiceLiveConfigSource());

        builder.Services.AddVoxa(builder.Configuration, voxa =>
        {
            voxa.AddProvider(WhisperCppDescriptors.Stt);
            voxa.AddProvider(PiperDescriptors.Tts);
            voxa.AddProvider(KokoroDescriptors.Tts);
            voxa.AddProvider(SileroVadDescriptors.Vad);
        });

        builder.Services.AddHttpContextAccessor();

        // VDX-007: Ada's engine drives the composed pipeline's agent stage. Scoped = one driver per
        // voice connection; the thread id is per-connection state (the in-chat mic passes
        // ?thread=<id> to continue the active text thread; otherwise voice gets its own thread,
        // created lazily on the first REAL user turn — never for a background result, whose
        // UserText is empty and must not mint a thread titled from nothing).
        builder.Services.AddScoped<IAgentTurnDriver>(sp =>
        {
            var engine = sp.GetRequiredService<IAdaEngine>();
            var convos = sp.GetService<IConversationStore>();
            var vlog = sp.GetService<ILoggerFactory>()?.CreateLogger("Ada.Voice.Turn");

            var http = sp.GetService<IHttpContextAccessor>()?.HttpContext;
            var threadId = http?.Request.Query["thread"].FirstOrDefault();
            if (string.IsNullOrWhiteSpace(threadId) || convos?.Load(threadId) is null) threadId = null;

            string? ThreadIdForVoiceTurn(VoiceTurnContext ctx)
            {
                if (ctx.Trigger != Voxa.Frames.TurnTrigger.UserUtterance) return threadId;
                vlog?.LogInformation("[voice] agent INVOKED — transcript: {Text}", ctx.UserText);
                if (convos is null) return null;
                threadId ??= convos.Create(VoiceTitle(ctx.UserText)).Id;
                return threadId;
            }

            return new AdaEngineTurnDriver(engine, ThreadIdForVoiceTurn, vlog);
        });

        // VDX-008: the M10 thinker. Registering it is the whole opt-in — the composer inserts the
        // background stage and the loop's arbitration; AdaEngineTurnDriver emits the requests.
        builder.Services.AddVoxaBackgroundAgent(sp => new AdaBackgroundTurnDriver(
            sp.GetRequiredKeyedService<IAdaEngine>(AdaCoreServiceCollectionExtensions.ThinkerEngineKey),
            sp.GetService<ILoggerFactory>()?.CreateLogger("Ada.Voice.Thinker")));
    }

    /// <summary>Maps the loopback voice endpoint (Voxa-composed) and the Settings → Voice API.</summary>
    public static void MapAdaVoice(WebApplication app)
    {
        app.UseWebSockets();
        // The whole M6.1 hand-built chain, retired (Path B): Voxa's composer now owns VAD → STT →
        // filter → Ada's driver → aggregation → TTS, plus the background stage for the M10 thinker.
        app.MapVoxaVoice("/voice").UseDefaults();

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

        // Change the pipeline (STT model / TTS engine / voice). Model/language/voice apply on the next
        // voice session (the live config source); switching the TTS provider needs an app restart.
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

    private static string VoiceTitle(string userText)
    {
        var t = (userText ?? string.Empty).Trim().ReplaceLineEndings(" ");
        if (t.Length == 0) return "Voice chat";
        return t.Length > 60 ? t[..60].TrimEnd() + "…" : t;
    }
}

/// <summary>
/// Live per-connection knobs (Settings → Voice) for keys the composer reads at COMPOSE time — the
/// hand-built chain used to rebuild its config per connection; this source preserves that behavior
/// under <c>UseDefaults()</c>. Reads <see cref="ConfigStore"/> on every lookup (a few key reads per
/// new connection). Sits above the startup defaults, below real config/env.
/// </summary>
internal sealed class AdaVoiceLiveConfigSource : ConfigurationProvider, IConfigurationSource
{
    public IConfigurationProvider Build(IConfigurationBuilder builder) => this;

    public override bool TryGet(string key, out string? value)
    {
        value = key switch
        {
            "Voxa:WhisperCpp:Model" => new ConfigStore().Load().SttModel,
            "Voxa:WhisperCpp:Language" => new ConfigStore().Load().SttLanguage,
            "Voxa:Piper:Voice" => Voice(kokoro: false),
            "Voxa:Kokoro:Voice" => Voice(kokoro: true),
            _ => null,
        };
        return value is not null;

        static string? Voice(bool kokoro)
        {
            var cfg = new ConfigStore().Load();
            var isKokoro = string.Equals(cfg.TtsProvider, "Kokoro", StringComparison.OrdinalIgnoreCase);
            return isKokoro == kokoro ? cfg.TtsVoice : null; // fall through to the startup default otherwise
        }
    }
}

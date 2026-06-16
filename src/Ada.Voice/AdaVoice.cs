using Ada.Core;
using Microsoft.AspNetCore.Builder;
using Microsoft.Agents.AI;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Configuration.Memory;
using Microsoft.Extensions.DependencyInjection;
using Voxa.AspNetCore;
using Voxa.Audio.SileroVad;
using Voxa.Speech.Piper;
using Voxa.Speech.WhisperCpp;

namespace Ada.Voice;

/// <summary>
/// Ada's voice plane (M6): hosts the Voxa pipeline in-process on the loopback server. The default
/// path is fully local — Silero VAD, WhisperCpp STT, Piper TTS — and the agent is the same Ada
/// (persona + skills + tools) the text surface uses. Barge-in/interruption is handled by Voxa.
/// </summary>
public static class AdaVoice
{
    /// <summary>
    /// Ada's local voice defaults, pinned so the pipeline works out of the box with no appsettings.json
    /// and so the first push-to-talk doesn't stall on a model download. All keys are verified against
    /// Voxa 0.5.0-alpha: each provider also has built-in defaults, but we choose intentionally here —
    /// the low-latency profile for snappy push-to-talk, English WhisperCpp + Piper, embedded Silero VAD,
    /// and eager warmup so the speech models download + load at server start, not on the first press.
    /// Inserted as the lowest-priority source, so a user's appsettings.json / env vars still override.
    /// </summary>
    private static readonly Dictionary<string, string?> VoiceDefaults = new()
    {
        ["Voxa:Profile"] = "LowLatency",
        ["Voxa:Stt"] = "WhisperCpp",
        ["Voxa:Tts"] = "Piper",
        ["Voxa:WhisperCpp:Model"] = "base.en",   // tiny(.en)|base(.en)|small(.en); base.en = good size/accuracy, English
        ["Voxa:WhisperCpp:Language"] = "en",
        ["Voxa:Piper:Voice"] = "en_US-lessac-medium",
        ["Voxa:Vad:Engine"] = "Silero",
        ["Voxa:Models:EagerWarmup"] = "true",    // pre-download + load at startup; first caller pays nothing
        ["Voxa:Agent:Provider"] = "Echo",        // Ada supplies her own agent in the pipeline; keep Voxa's keyless so AddVoxa never demands a cloud key
    };

    /// <summary>Registers Voxa with Ada's local speech stack. Models auto-download on first use.</summary>
    public static void AddAdaVoice(WebApplicationBuilder builder)
    {
        // Lowest-priority defaults: real config (appsettings.json, Voxa__* env vars) still wins.
        builder.Configuration.Sources.Insert(0, new MemoryConfigurationSource { InitialData = VoiceDefaults });

        builder.Services.AddVoxa(builder.Configuration, voxa =>
        {
            voxa.AddProvider(WhisperCppDescriptors.Stt);
            voxa.AddProvider(PiperDescriptors.Tts);
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
            pipeline
                .UseSilenceGate()
                .UseSpeechToText(() => WhisperCppDescriptors.Stt.CreateProcessor(app.Services, app.Configuration.GetSection("Voxa")))
                .UseTranscriptionFilter()
                .UseMicrosoftAgent(agent)
                .UseSentenceAggregator()
                .UseTextToSpeech(() => PiperDescriptors.Tts.CreateProcessor(app.Services, app.Configuration.GetSection("Voxa")));
        });
    }
}

using Ada.Core;
using Microsoft.AspNetCore.Builder;
using Microsoft.Agents.AI;
using Microsoft.Extensions.Configuration;
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
    /// <summary>Registers Voxa with Ada's local speech stack. Models auto-download on first use.</summary>
    public static void AddAdaVoice(WebApplicationBuilder builder)
    {
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

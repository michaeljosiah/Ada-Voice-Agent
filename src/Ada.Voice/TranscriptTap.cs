using Microsoft.Extensions.Logging;
using Voxa.Frames;       // Frame, AudioRawFrame, TranscriptionFrame
using Voxa.Processors;   // FrameProcessor

namespace Ada.Voice;

/// <summary>
/// Diagnostic pass-through tap for the voice input stage. Logs, at this pipeline position: the first audio
/// frame (with its sample rate — the VAD only gates at its configured rate, so a wrong rate here = no VAD =
/// no final transcript), each final transcript, and the first occurrence of every other (control) frame type
/// — e.g. the VAD's speech-started / speech-stopped frames. Lets a stalled voice turn be pinned to the exact
/// stage. Passes every frame through unchanged.
/// </summary>
internal sealed class TranscriptTap(string stage, ILogger? log) : FrameProcessor
{
    private bool _sawAudio;
    private readonly HashSet<string> _seen = new();

    protected override async ValueTask ProcessFrameAsync(Frame frame, CancellationToken ct)
    {
        switch (frame)
        {
            case AudioRawFrame a:
                if (!_sawAudio) { _sawAudio = true; log?.LogInformation("[voice] {Stage}: audio flowing (rate={Rate} Hz)", stage, a.SampleRate); }
                break;
            case TranscriptionFrame { IsFinal: true } t:
                log?.LogInformation("[voice] {Stage}: FINAL transcript: {Text}", stage, t.Text);
                break;
            case TranscriptionFrame:
                break; // skip partials (too noisy)
            default:
                var name = frame.GetType().Name;
                // Surface the reply text the agent emits (and what reaches TTS) so a lost/empty reply is visible.
                var text = frame.GetType().GetProperty("Text")?.GetValue(frame) as string;
                if (!string.IsNullOrEmpty(text)) log?.LogInformation("[voice] {Stage}: {Frame} «{Text}»", stage, name, text);
                else if (_seen.Add(name)) log?.LogInformation("[voice] {Stage}: {Frame}", stage, name); // e.g. UserStoppedSpeakingFrame
                break;
        }
        await PushFrameAsync(frame, ct).ConfigureAwait(false);
    }
}

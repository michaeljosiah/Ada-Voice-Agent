using Voxa.Frames;       // Frame, AudioRawFrame
using Voxa.Processors;   // FrameProcessor

namespace Ada.Voice;

/// <summary>
/// Stamps inbound <see cref="AudioRawFrame"/>s with the sample rate the browser actually sends
/// (16 kHz — see wwwroot <c>VOICE_IN_RATE</c> / "Whisper needs exactly 16 kHz"). Ada drives the pipeline
/// through the fluent <c>.Use(...)</c> route, which never sets Voxa's (internal) session input rate, so the
/// <c>WebSocketAudioSource</c> defaults to 24 kHz. The Silero VAD only gates when the frame rate matches its
/// configured rate (otherwise it forwards audio untouched — i.e. no VAD at all), so this corrects the label
/// before the VAD — and hands Whisper an honest rate too. A no-op once a frame already carries the target
/// rate; reuses the existing PCM buffer (no copy). Mirrors <see cref="BlankTranscriptionFilter"/>.
/// </summary>
internal sealed class InputRateTagProcessor : FrameProcessor
{
    private readonly int _rate;

    public InputRateTagProcessor(int rate) : base("InputRateTag") => _rate = rate;

    protected override async ValueTask ProcessFrameAsync(Frame frame, CancellationToken ct)
    {
        if (frame is AudioRawFrame a && a.SampleRate != _rate)
            frame = new AudioRawFrame(a.Pcm, _rate, a.Channels); // reuse the same PCM memory
        await PushFrameAsync(frame, ct).ConfigureAwait(false);
    }
}

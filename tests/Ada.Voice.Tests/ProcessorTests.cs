using Ada.Voice;
using Voxa.Frames;
using Voxa.Processors;

namespace Ada.Voice.Tests;

/// <summary>
/// Drives Ada's two custom pipeline stages through a real Voxa processor chain (link → start → queue):
/// the rate-tag that makes the Silero VAD actually gate, and the blank-audio filter that keeps non-speech
/// out of the agent. These are the stages the M6.1 fix introduced/relies on.
/// </summary>
public class ProcessorTests
{
    /// <summary>A terminal stage that records every frame it receives and signals when one matches.</summary>
    private sealed class Capture(Func<Frame, bool> until) : FrameProcessor("capture")
    {
        public readonly List<Frame> Frames = [];
        private readonly TaskCompletionSource _hit = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public Task Hit => _hit.Task;
        protected override async ValueTask ProcessFrameAsync(Frame frame, CancellationToken ct)
        {
            Frames.Add(frame);
            if (until(frame)) _hit.TrySetResult();
            await PushFrameAsync(frame, ct).ConfigureAwait(false);
        }
    }

    /// <summary>Link head → capture, start both loops, push a StartFrame then the frames, await the match.</summary>
    private static async Task<Capture> RunAsync(FrameProcessor head, Func<Frame, bool> until, params Frame[] frames)
    {
        var capture = new Capture(until);
        head.Link(capture);
        using var cts = new CancellationTokenSource();
        head.Start(cts.Token);
        capture.Start(cts.Token);
        await head.QueueFrameAsync(new StartFrame(16000, 1), cts.Token);
        foreach (var f in frames) await head.QueueFrameAsync(f, cts.Token);
        await capture.Hit.WaitAsync(TimeSpan.FromSeconds(10));
        return capture;
    }

    [Fact]
    public async Task RateTag_relabels_mismatched_audio_to_the_target_rate()
    {
        // The WebSocket source tags inbound frames 24 kHz by default; Silero only gates at its own rate.
        var capture = await RunAsync(new InputRateTagProcessor(16000),
            f => f is AudioRawFrame,
            new AudioRawFrame(new byte[320], 24000, 1));

        var audio = capture.Frames.OfType<AudioRawFrame>().Single();
        Assert.Equal(16000, audio.SampleRate);
        Assert.Equal(1, audio.Channels);
        Assert.Equal(320, audio.Pcm.Length);   // same PCM, only the label changed
    }

    [Fact]
    public async Task RateTag_passes_audio_already_at_the_target_rate_untouched()
    {
        var capture = await RunAsync(new InputRateTagProcessor(16000),
            f => f is AudioRawFrame,
            new AudioRawFrame(new byte[160], 16000, 1));

        Assert.Equal(16000, capture.Frames.OfType<AudioRawFrame>().Single().SampleRate);
    }

    [Fact]
    public async Task BlankFilter_drops_the_blank_marker_but_passes_real_speech()
    {
        // Queue a non-speech marker (must be dropped) then a real final (must pass). The match is "Hi",
        // so by the time it arrives the blank has already been processed-and-dropped ahead of it.
        var capture = await RunAsync(new BlankTranscriptionFilter(),
            f => f is TranscriptionFrame { IsFinal: true, Text: "Hi" },
            new TranscriptionFrame("[BLANK_AUDIO]", true, "en", ""),
            new TranscriptionFrame("Hi", true, "en", ""));

        var finals = capture.Frames.OfType<TranscriptionFrame>().Where(t => t.IsFinal).Select(t => t.Text).ToList();
        Assert.Contains("Hi", finals);
        Assert.DoesNotContain("[BLANK_AUDIO]", finals);
    }
}

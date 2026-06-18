using Voxa.Frames;       // Frame, TranscriptionFrame
using Voxa.Processors;   // FrameProcessor

namespace Ada.Voice;

/// <summary>
/// Drops whisper.cpp's non-speech markers — <c>[BLANK_AUDIO]</c>, <c>[ Silence ]</c>, <c>(music)</c>,
/// punctuation-only blips — so silence or background noise never reaches the agent. It chains after
/// Voxa's built-in <c>TranscriptionFilter</c> (which catches hallucinations like "thank you" but NOT
/// the bracketed blank-audio marker). Any wholly bracketed/parenthesised/asterisked final transcript,
/// or one with no letters or digits, is treated as non-speech and not forwarded.
/// </summary>
internal sealed class BlankTranscriptionFilter : FrameProcessor
{
    protected override async ValueTask ProcessFrameAsync(Frame frame, CancellationToken ct)
    {
        if (frame is TranscriptionFrame { IsFinal: true } t && IsNonSpeech(t.Text))
            return; // drop — never forwarded downstream, so no agent turn and nothing shown
        await PushFrameAsync(frame, ct).ConfigureAwait(false);
    }

    private static bool IsNonSpeech(string? text)
    {
        var s = (text ?? string.Empty).Trim();
        if (s.Length == 0) return true;
        if ((s[0] == '[' && s[^1] == ']') || (s[0] == '(' && s[^1] == ')') || (s[0] == '*' && s[^1] == '*'))
            return true; // whisper emits non-speech as a wholly bracketed marker, e.g. [BLANK_AUDIO]
        foreach (var c in s) if (char.IsLetterOrDigit(c)) return false;
        return true; // nothing but punctuation/whitespace
    }
}

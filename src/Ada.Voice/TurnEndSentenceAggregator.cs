using System.Text;
using Voxa.Frames;
using Voxa.Processors;

namespace Ada.Voice;

/// <summary>
/// Buffers streamed LLM text into speakable chunks and flushes the final fragment at the LLM turn boundary.
/// Voxa's package aggregator flushes on connection end, but Ada keeps the voice socket open across turns.
/// </summary>
internal sealed class TurnEndSentenceAggregator : FrameProcessor
{
    private readonly StringBuilder _buffer = new();
    private readonly object _lock = new();
    private int _lastBoundary = -1;
    private bool _firstFlushOfTurn = true;

    public int MaxBufferChars { get; init; } = 500;
    public int EagerFirstChunkMinChars { get; init; }

    public TurnEndSentenceAggregator() : base("TurnEndSentenceAggregator") { }

    protected override async ValueTask ProcessFrameAsync(Frame frame, CancellationToken ct)
    {
        switch (frame)
        {
            case LlmTextChunkFrame { Text: { Length: > 0 } text }:
                var chunk = Append(text);
                if (!string.IsNullOrEmpty(chunk))
                    await PushFrameAsync(new TextFrame(chunk), ct).ConfigureAwait(false);
                return;

            case LlmTurnStartedFrame:
                lock (_lock) _firstFlushOfTurn = true;
                break;

            case LlmTurnEndedFrame:
                var final = Flush(resetTurn: true);
                if (!string.IsNullOrEmpty(final))
                    await PushFrameAsync(new TextFrame(final), ct).ConfigureAwait(false);
                break;

            case UserStartedSpeakingFrame or InterruptionFrame:
                Clear();
                break;
        }

        await PushFrameAsync(frame, ct).ConfigureAwait(false);
    }

    protected override async ValueTask OnEndAsync(EndFrame frame, CancellationToken ct)
    {
        var final = Flush(resetTurn: true);
        if (!string.IsNullOrEmpty(final))
            await PushFrameAsync(new TextFrame(final), ct).ConfigureAwait(false);
    }

    private string? Append(string text)
    {
        lock (_lock)
        {
            var start = _buffer.Length;
            _buffer.Append(text);

            if (start > 0 && char.IsWhiteSpace(text[0]) && IsBoundaryChar(_buffer[start - 1]))
                _lastBoundary = start - 1;
            for (var i = 0; i < text.Length - 1; i++)
                if (IsBoundaryChar(text[i]) && char.IsWhiteSpace(text[i + 1]))
                    _lastBoundary = start + i;
            if (IsBoundaryChar(text[^1]))
                _lastBoundary = _buffer.Length - 1;

            if (_lastBoundary < 0 && _firstFlushOfTurn && EagerFirstChunkMinChars > 0 && _buffer.Length >= EagerFirstChunkMinChars)
            {
                if (start > 0 && char.IsWhiteSpace(text[0]) && IsClauseChar(_buffer[start - 1]))
                    _lastBoundary = start - 1;
                for (var i = 0; i < text.Length - 1; i++)
                    if (IsClauseChar(text[i]) && char.IsWhiteSpace(text[i + 1]))
                        _lastBoundary = start + i;
            }

            if (_lastBoundary >= 0)
                return TakeThroughBoundary();

            if (_buffer.Length >= MaxBufferChars)
                return TakeAll(resetTurn: false);

            return null;
        }
    }

    private string? Flush(bool resetTurn)
    {
        lock (_lock) return TakeAll(resetTurn);
    }

    private void Clear()
    {
        lock (_lock)
        {
            _buffer.Clear();
            _lastBoundary = -1;
            _firstFlushOfTurn = true;
        }
    }

    private string? TakeThroughBoundary()
    {
        var text = _buffer.ToString(0, _lastBoundary + 1).Trim();
        _buffer.Remove(0, _lastBoundary + 1);
        _lastBoundary = -1;
        _firstFlushOfTurn = false;
        return text.Length == 0 ? null : text;
    }

    private string? TakeAll(bool resetTurn)
    {
        var text = _buffer.Length > 0 ? _buffer.ToString().Trim() : null;
        _buffer.Clear();
        _lastBoundary = -1;
        _firstFlushOfTurn = resetTurn;
        return string.IsNullOrEmpty(text) ? null : text;
    }

    private static bool IsBoundaryChar(char c) => c is '.' or '!' or '?' or '\n';
    private static bool IsClauseChar(char c) => c is ',' or ';' or ':';
}

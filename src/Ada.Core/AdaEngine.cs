namespace Ada.Core;

/// <summary>A single turn request to Ada. <paramref name="ThreadId"/> names the conversation to append to
/// and persist; when null, the engine uses its in-process history (CLI one-shots, voice, tests).</summary>
public sealed record AdaRequest(string Message, string? ThreadId = null);

/// <summary>
/// One streamed piece of Ada's reply. <see cref="Route"/> names where the turn was served
/// (e.g. "local", "echo", "Claude · web") so the UI can show a route badge — a first-class
/// part of the "private by default" promise: every escalation is visible.
/// </summary>
public sealed record AdaResponseChunk(string Text, string Route = "local", bool IsFinal = false);

/// <summary>
/// The conversation engine. Every host — the WebView2 shell, the CLI, the voice pipeline —
/// talks to Ada through this one seam. M0 ships an echo implementation; later milestones swap
/// in the real Agent Framework agent without changing this contract.
/// </summary>
public interface IAdaEngine
{
    IAsyncEnumerable<AdaResponseChunk> RespondAsync(AdaRequest request, CancellationToken ct = default);
}

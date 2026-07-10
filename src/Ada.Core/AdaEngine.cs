namespace Ada.Core;

/// <summary>A single turn request to Ada. <paramref name="ThreadId"/> names the conversation to append to
/// and persist; when null, the engine uses its in-process history (CLI one-shots, voice, tests).
/// <paramref name="AllowDelegation"/> (M10): a turn that would enter the slow tool path may instead be
/// handed to the background thinker — set by the voice plane, never by text surfaces, so their inline
/// tool flow is untouched. <paramref name="ChatOnly"/> (M10): force the fast chat agent regardless of
/// tool heuristics — used when delivering a background result, which must never re-enter tool mode.</summary>
public sealed record AdaRequest(string Message, string? ThreadId = null, bool AllowDelegation = false, bool ChatOnly = false);

/// <summary>What a streamed chunk is (M10). <see cref="Answer"/> is spoken/rendered reply text;
/// <see cref="Delegate"/> hands the turn's goal to the background thinker — voice translates it into
/// a Voxa frame, text surfaces ignore it (they never set <c>AllowDelegation</c>, so they never see one).</summary>
public enum AdaResponseChunkKind { Answer, Delegate }

/// <summary>
/// One streamed piece of Ada's reply. <see cref="Route"/> names where the turn was served
/// (e.g. "local", "echo", "Claude · web") so the UI can show a route badge — a first-class
/// part of the "private by default" promise: every escalation is visible.
/// <see cref="Kind"/> (M10) distinguishes reply text from a delegation hand-off, whose
/// <see cref="Goal"/>/<see cref="ContextSummary"/> carry what the thinker should do.
/// </summary>
public sealed record AdaResponseChunk(
    string Text,
    string Route = "local",
    bool IsFinal = false,
    AdaResponseChunkKind Kind = AdaResponseChunkKind.Answer,
    string? Goal = null,
    string? ContextSummary = null);

/// <summary>
/// The conversation engine. Every host — the WebView2 shell, the CLI, the voice pipeline —
/// talks to Ada through this one seam. M0 ships an echo implementation; later milestones swap
/// in the real Agent Framework agent without changing this contract.
/// </summary>
public interface IAdaEngine
{
    IAsyncEnumerable<AdaResponseChunk> RespondAsync(AdaRequest request, CancellationToken ct = default);
}

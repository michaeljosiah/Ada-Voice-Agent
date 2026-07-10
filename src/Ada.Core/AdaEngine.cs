namespace Ada.Core;

/// <summary>A single turn request to Ada. <paramref name="ThreadId"/> names the conversation to append to
/// and persist; when null, the engine uses its in-process history (CLI one-shots, voice, tests).
/// <paramref name="AllowDelegation"/> (M10): a turn that would enter the slow tool path may instead be
/// handed to the background thinker — set by the voice plane, never by text surfaces, so their inline
/// tool flow is untouched. <paramref name="ChatOnly"/> (M10): force the fast chat agent regardless of
/// tool heuristics — used when delivering a background result, which must never re-enter tool mode.
/// <paramref name="PersistUserMessage"/> (M10): whether to record <see cref="Message"/> as a user turn.
/// False for a background-result delivery — its message is a synthetic "[System note…]" prompt that must
/// not land in the durable thread and contaminate future context; only the spoken reply is recorded.</summary>
public sealed record AdaRequest(
    string Message, string? ThreadId = null, bool AllowDelegation = false,
    bool ChatOnly = false, bool PersistUserMessage = true);

/// <summary>
/// What a streamed chunk carries. <see cref="Answer"/> is user-visible reply text — the only kind voice
/// speaks or the engine persists as the assistant answer. <see cref="Thinking"/> / <see cref="Tool"/> /
/// <see cref="Status"/> are process metadata for UI state (never spoken or persisted).
/// <see cref="Delegation"/> is a UI status badge for a sub-agent/skill hand-off inside the tool loop.
/// <see cref="Delegate"/> (M10) is a different thing — the talker→thinker background hand-off: voice
/// translates it into a Voxa <c>BackgroundTaskRequestFrame</c>, and text surfaces ignore it (they never
/// set <c>AllowDelegation</c>, so they never see one).
/// </summary>
public enum AdaResponseChunkKind
{
    Answer,
    Thinking,
    Tool,
    Delegation,
    Status,
    Delegate,
}

/// <summary>
/// One streamed piece of Ada's reply. Answer chunks are user-visible text; other kinds are process
/// metadata for UI state and must not be spoken or persisted as assistant answer text.
/// <see cref="Route"/> names where the turn was served (e.g. "local", "echo", "Claude · web") so
/// the UI can show a route badge — a first-class part of the "private by default" promise.
/// <see cref="Label"/> is a short UI tag for a status chunk; <see cref="Goal"/> /
/// <see cref="ContextSummary"/> (M10) carry the task and its context for a
/// <see cref="AdaResponseChunkKind.Delegate"/> hand-off.
/// </summary>
public sealed record AdaResponseChunk(
    string Text,
    string Route = "local",
    bool IsFinal = false,
    AdaResponseChunkKind Kind = AdaResponseChunkKind.Answer,
    string? Label = null,
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

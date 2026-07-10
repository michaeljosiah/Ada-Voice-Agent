using System.Runtime.CompilerServices;
using Ada.Core;
using Microsoft.Extensions.Logging;
using Voxa.Frames;
using Voxa.Processors;

namespace Ada.Voice;

/// <summary>
/// The M10 thinker: runs delegated goals through a FRESH background engine per task (heavyweight
/// route, full tool harness, its own history). A fresh engine per task is deliberate (Codex #4):
/// the background processor can run tasks concurrently, and one shared engine would race on its
/// mutable in-process history and bleed research context across tasks and sessions — the latter a
/// "private by default" violation. Its text is never spoken directly — Voxa accumulates it into the
/// task result the talker delivers — so no sanitizer here; the <see cref="StatusFrame"/> gives the
/// UI a progress line. Gated tools fail fast inside <see cref="ToolContext.EnterBackgroundScope"/>:
/// nobody is watching an approval card for delegated work (M10-T6).
/// </summary>
internal sealed class AdaBackgroundTurnDriver(Func<IAdaEngine> thinkerFactory, ILogger? log = null) : IAgentTurnDriver
{
    public async IAsyncEnumerable<Frame> RunTurnAsync(
        VoiceTurnContext ctx,
        [EnumeratorCancellation] CancellationToken ct)
    {
        yield return new StatusFrame("Working on it in the background…");
        log?.LogInformation("[thinker] task started: {Goal}", ctx.UserText);

        // Prepend the delegating turn's conversation context (Codex #1) so the goal resolves
        // "that file" / pronouns against what the user was actually discussing.
        var context = ctx.GetMetadata<string>(BackgroundAgentProcessor.ContextJsonMetadataKey);
        var message = string.IsNullOrWhiteSpace(context)
            ? ctx.UserText
            : $"Recent conversation for context:\n{context}\n\nTask: {ctx.UserText}";

        var thinker = thinkerFactory(); // fresh engine → isolated history per task
        using var backgroundScope = ToolContext.EnterBackgroundScope();
        await foreach (var chunk in thinker.RespondAsync(new AdaRequest(message), ct).ConfigureAwait(false))
        {
            if (chunk.IsFinal || chunk.Kind != AdaResponseChunkKind.Answer) continue;
            if (string.IsNullOrEmpty(chunk.Text)) continue;
            yield return new LlmTextChunkFrame(chunk.Text);
        }
        log?.LogInformation("[thinker] task finished: {Goal}", ctx.UserText);
    }
}

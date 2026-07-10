using System.Runtime.CompilerServices;
using Ada.Core;
using Microsoft.Extensions.Logging;
using Voxa.Frames;
using Voxa.Processors;

namespace Ada.Voice;

/// <summary>
/// The M10 thinker: runs delegated goals through Ada's background engine (heavyweight route, full
/// tool harness, its own history). Its text is never spoken directly — Voxa accumulates it into the
/// task result the talker delivers — so no sanitizer here; the <see cref="StatusFrame"/> gives the
/// UI a progress line. Gated tools fail fast inside <see cref="ToolContext.EnterBackgroundScope"/>:
/// nobody is watching an approval card for delegated work (M10-T6).
/// </summary>
internal sealed class AdaBackgroundTurnDriver(IAdaEngine thinker, ILogger? log = null) : IAgentTurnDriver
{
    public async IAsyncEnumerable<Frame> RunTurnAsync(
        VoiceTurnContext ctx,
        [EnumeratorCancellation] CancellationToken ct)
    {
        yield return new StatusFrame("Working on it in the background…");
        log?.LogInformation("[thinker] task started: {Goal}", ctx.UserText);

        using var backgroundScope = ToolContext.EnterBackgroundScope();
        await foreach (var chunk in thinker.RespondAsync(new AdaRequest(ctx.UserText), ct).ConfigureAwait(false))
        {
            if (chunk.IsFinal || chunk.Kind != AdaResponseChunkKind.Answer) continue;
            if (string.IsNullOrEmpty(chunk.Text)) continue;
            yield return new LlmTextChunkFrame(chunk.Text);
        }
        log?.LogInformation("[thinker] task finished: {Goal}", ctx.UserText);
    }
}

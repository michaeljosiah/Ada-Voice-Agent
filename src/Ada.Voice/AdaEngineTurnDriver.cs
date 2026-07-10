using System.Runtime.CompilerServices;
using Ada.Core;
using Microsoft.Extensions.Logging;
using Voxa.Frames;
using Voxa.Processors;
using Voxa.Services.MicrosoftAgents;

namespace Ada.Voice;

/// <summary>
/// Drives voice turns through Ada's canonical engine path, matching the text UI and CLI behavior —
/// plus the M10 talker/thinker split: a turn the engine hands off surfaces as a
/// <see cref="BackgroundTaskRequestFrame"/> (Voxa runs it on the thinker), and a completed
/// background result re-enters here as a <see cref="TurnTrigger.BackgroundResult"/> turn the
/// engine gates for relevance.
/// </summary>
internal sealed class AdaEngineTurnDriver(
    IAdaEngine engine,
    Func<VoiceTurnContext, string?> threadIdForTurn,
    ILogger? log = null) : IAgentTurnDriver
{
    public async IAsyncEnumerable<Frame> RunTurnAsync(
        VoiceTurnContext ctx,
        [EnumeratorCancellation] CancellationToken ct)
    {
        // A background result carries no user text — feed the result framed by the relevance-gate
        // instruction (Voxa's canonical wording) so the engine can deliver it or stay silent.
        // ChatOnly: a result delivery must never re-enter the tool path its content might keyword-match.
        var request = ctx.Trigger == TurnTrigger.BackgroundResult && ctx.BackgroundResult is { } result
            ? new AdaRequest(
                MicrosoftAgentVoice.CreateBackgroundResultMessage(result).Text ?? string.Empty,
                threadIdForTurn(ctx),
                ChatOnly: true)
            : new AdaRequest(ctx.UserText, threadIdForTurn(ctx), AllowDelegation: true);

        await foreach (var chunk in engine.RespondAsync(request, ct).ConfigureAwait(false))
        {
            if (chunk.IsFinal) continue;
            switch (chunk.Kind)
            {
                case AdaResponseChunkKind.Delegate when chunk.Goal is { Length: > 0 } goal:
                    // The engine handed this turn to the thinker — Voxa's BackgroundAgentProcessor
                    // picks the request up downstream and the result re-enters as a new turn.
                    log?.LogInformation("[voice] delegating to the thinker: {Goal}", goal);
                    yield return new BackgroundTaskRequestFrame(
                        Guid.NewGuid().ToString("N"), goal, chunk.ContextSummary, ctx.TurnId);
                    break;

                case AdaResponseChunkKind.Answer when !string.IsNullOrEmpty(chunk.Text):
                    var text = VoiceTextSanitizer.Sanitize(chunk.Text);
                    if (string.IsNullOrWhiteSpace(text)) continue;
                    log?.LogDebug("[voice] engine chunk: {Text}", text);
                    yield return new LlmTextChunkFrame(text);
                    break;
            }
        }
    }
}

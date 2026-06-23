using System.Runtime.CompilerServices;
using Ada.Core;
using Microsoft.Extensions.Logging;
using Voxa.Frames;
using Voxa.Processors;

namespace Ada.Voice;

/// <summary>Drives voice turns through Ada's canonical engine path, matching the text UI and CLI behavior.</summary>
internal sealed class AdaEngineTurnDriver(
    IAdaEngine engine,
    Func<VoiceTurnContext, string?> threadIdForTurn,
    ILogger? log = null) : IAgentTurnDriver
{
    public async IAsyncEnumerable<Frame> RunTurnAsync(
        VoiceTurnContext ctx,
        [EnumeratorCancellation] CancellationToken ct)
    {
        await foreach (var chunk in engine.RespondAsync(new AdaRequest(ctx.UserText, threadIdForTurn(ctx)), ct).ConfigureAwait(false))
        {
            if (chunk.IsFinal) continue;
            if (string.IsNullOrEmpty(chunk.Text)) continue;
            log?.LogDebug("[voice] engine chunk: {Text}", chunk.Text);
            yield return new LlmTextChunkFrame(chunk.Text);
        }
    }
}

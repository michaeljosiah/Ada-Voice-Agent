using System.Runtime.CompilerServices;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;

namespace Ada.Core;

/// <summary>
/// Ada's real brain: a Microsoft Agent Framework <see cref="ChatClientAgent"/> over an
/// <see cref="IChatClient"/>, primed with the persona. It streams the reply and tags every chunk
/// with a route. If the chat client is route-aware (the hybrid router), the badge reflects the
/// backend that actually served the turn — so escalation is always visible.
/// </summary>
public sealed class AgentEngine : IAdaEngine
{
    private readonly IChatClient _chatClient;
    private readonly ChatClientAgent _agent;
    private readonly string _route;

    public AgentEngine(IChatClient chatClient, Persona persona, string route = "local", IEnumerable<AITool>? tools = null)
    {
        ArgumentNullException.ThrowIfNull(chatClient);
        ArgumentNullException.ThrowIfNull(persona);

        _chatClient = chatClient;
        _route = route;

        var toolList = tools?.ToList();
        _agent = new ChatClientAgent(chatClient, instructions: persona.Instructions, name: "Ada",
            tools: toolList is { Count: > 0 } ? toolList : null);
    }

    private string CurrentRoute => (_chatClient as IRouteAware)?.CurrentRoute ?? _route;

    public async IAsyncEnumerable<AdaResponseChunk> RespondAsync(
        AdaRequest request, [EnumeratorCancellation] CancellationToken ct = default)
    {
        await foreach (var update in _agent.RunStreamingAsync(request.Message, cancellationToken: ct).ConfigureAwait(false))
        {
            if (!string.IsNullOrEmpty(update.Text))
                yield return new AdaResponseChunk(update.Text, CurrentRoute);
        }

        yield return new AdaResponseChunk(string.Empty, CurrentRoute, IsFinal: true);
    }
}

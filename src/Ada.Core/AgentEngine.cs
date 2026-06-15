using System.Runtime.CompilerServices;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;

namespace Ada.Core;

/// <summary>
/// Ada's real brain (M1): a Microsoft Agent Framework <see cref="ChatClientAgent"/> over an
/// <see cref="IChatClient"/>, primed with the persona. It streams the reply and tags every chunk
/// with a route ("local", "local · foundry") so the UI's route badge is honest about where the
/// turn ran. Multi-turn conversation state (sessions) is layered on in M4.
/// </summary>
public sealed class AgentEngine : IAdaEngine
{
    private readonly ChatClientAgent _agent;
    private readonly string _route;

    public AgentEngine(IChatClient chatClient, Persona persona, string route = "local")
    {
        ArgumentNullException.ThrowIfNull(chatClient);
        ArgumentNullException.ThrowIfNull(persona);

        _agent = new ChatClientAgent(chatClient, instructions: persona.Instructions, name: "Ada");
        _route = route;
    }

    public async IAsyncEnumerable<AdaResponseChunk> RespondAsync(
        AdaRequest request, [EnumeratorCancellation] CancellationToken ct = default)
    {
        await foreach (var update in _agent.RunStreamingAsync(request.Message, cancellationToken: ct).ConfigureAwait(false))
        {
            if (!string.IsNullOrEmpty(update.Text))
                yield return new AdaResponseChunk(update.Text, _route);
        }

        yield return new AdaResponseChunk(string.Empty, _route, IsFinal: true);
    }
}

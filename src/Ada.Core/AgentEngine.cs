using System.Runtime.CompilerServices;
using System.Text;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;

namespace Ada.Core;

/// <summary>
/// Ada's real brain: a Microsoft Agent Framework <see cref="ChatClientAgent"/> over an
/// <see cref="IChatClient"/>, primed with the persona. It keeps the running conversation, folds in
/// relevant memory each turn, streams the reply with an honest route badge, and compacts the history
/// so a long session stays inside the model's window without losing the thread.
/// </summary>
public sealed class AgentEngine : IAdaEngine
{
    private readonly IChatClient _chatClient;
    private readonly ChatClientAgent _agent;
    private readonly string _route;
    private readonly ITurnContext? _memory;
    private readonly ICompactionStrategy _compaction;
    private List<ChatMessage> _history = [];

    public AgentEngine(
        IChatClient chatClient,
        Persona persona,
        string route = "local",
        IEnumerable<AITool>? tools = null,
        ITurnContext? memory = null,
        ICompactionStrategy? compaction = null)
    {
        ArgumentNullException.ThrowIfNull(chatClient);
        ArgumentNullException.ThrowIfNull(persona);

        _chatClient = chatClient;
        _route = route;
        _memory = memory;
        _compaction = compaction ?? new NoCompaction();

        var toolList = tools?.ToList();
        _agent = new ChatClientAgent(chatClient, instructions: persona.Instructions, name: "Ada",
            tools: toolList is { Count: > 0 } ? toolList : null);
    }

    /// <summary>The running history length — exposed so compaction behaviour is observable in tests.</summary>
    public int HistoryMessageCount => _history.Count;

    public async IAsyncEnumerable<AdaResponseChunk> RespondAsync(
        AdaRequest request, [EnumeratorCancellation] CancellationToken ct = default)
    {
        // The turn: optional memory context (system), the running history, then the new user message.
        var turn = new List<ChatMessage>();
        if (_memory is not null)
        {
            var context = await _memory.BuildAsync(request.Message, ct).ConfigureAwait(false);
            if (!string.IsNullOrWhiteSpace(context))
                turn.Add(new ChatMessage(ChatRole.System, context));
        }
        turn.AddRange(_history);
        turn.Add(new ChatMessage(ChatRole.User, request.Message));

        var assistant = new StringBuilder();
        await foreach (var update in _agent.RunStreamingAsync(turn, cancellationToken: ct).ConfigureAwait(false))
        {
            if (!string.IsNullOrEmpty(update.Text))
            {
                assistant.Append(update.Text);
                yield return new AdaResponseChunk(update.Text, CurrentRoute);
            }
        }
        yield return new AdaResponseChunk(string.Empty, CurrentRoute, IsFinal: true);

        // Persist the turn and keep the window bounded.
        _history.Add(new ChatMessage(ChatRole.User, request.Message));
        _history.Add(new ChatMessage(ChatRole.Assistant, assistant.ToString()));
        _history = await _compaction.CompactAsync(_history, ct).ConfigureAwait(false);
    }

    private string CurrentRoute => (_chatClient as IRouteAware)?.CurrentRoute ?? _route;
}

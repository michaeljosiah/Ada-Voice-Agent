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
    private readonly IConversationStore? _conversations;
    private List<ChatMessage> _history = [];

    public AgentEngine(
        IChatClient chatClient,
        Persona persona,
        string route = "local",
        IEnumerable<AITool>? tools = null,
        ITurnContext? memory = null,
        ICompactionStrategy? compaction = null,
        AgentSkillsProvider? skills = null,
        IConversationStore? conversations = null)
    {
        ArgumentNullException.ThrowIfNull(chatClient);
        ArgumentNullException.ThrowIfNull(persona);

        _chatClient = chatClient;
        _route = route;
        _memory = memory;
        _compaction = compaction ?? new NoCompaction();
        _conversations = conversations;

        _agent = AdaAgentBuilder.Build(chatClient, persona.Instructions, tools?.ToList() ?? [], skills);
    }

    /// <summary>The running history length — exposed so compaction behaviour is observable in tests.</summary>
    public int HistoryMessageCount => _history.Count;

    public async IAsyncEnumerable<AdaResponseChunk> RespondAsync(
        AdaRequest request, [EnumeratorCancellation] CancellationToken ct = default)
    {
        var threaded = _conversations is not null && !string.IsNullOrEmpty(request.ThreadId);
        // In thread mode the stored transcript is the source of truth (and is never lossy); the in-process
        // _history serves the unthreaded path (CLI one-shots, voice, tests).
        var convo = threaded
            ? _conversations!.Load(request.ThreadId!) ?? _conversations.Create(Title(request.Message))
            : null;

        // The turn: optional memory context (system), the running history, then the new user message.
        var turn = new List<ChatMessage>();
        if (_memory is not null)
        {
            var context = await _memory.BuildAsync(request.Message, ct).ConfigureAwait(false);
            if (!string.IsNullOrWhiteSpace(context))
                turn.Add(new ChatMessage(ChatRole.System, context));
        }
        // Compaction shapes only what the model sees — never the stored transcript.
        turn.AddRange(threaded
            ? await _compaction.CompactAsync(convo!.Messages.Select(ToChatMessage).ToList(), ct).ConfigureAwait(false)
            : _history);
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

        if (threaded)
        {
            // Persist the full turn — the stored thread keeps everything; only the model context was bounded.
            var ts = DateTimeOffset.UtcNow.ToString("o");
            convo!.Messages.Add(new ConversationMessage { Role = "user", Text = request.Message, Ts = ts });
            convo.Messages.Add(new ConversationMessage { Role = "assistant", Text = assistant.ToString(), Route = CurrentRoute, Ts = ts });
            _conversations!.Save(convo);
        }
        else
        {
            // Unthreaded: keep the in-process window bounded (existing behaviour).
            _history.Add(new ChatMessage(ChatRole.User, request.Message));
            _history.Add(new ChatMessage(ChatRole.Assistant, assistant.ToString()));
            _history = await _compaction.CompactAsync(_history, ct).ConfigureAwait(false);
        }
    }

    private static ChatMessage ToChatMessage(ConversationMessage m) => new(
        m.Role switch { "assistant" => ChatRole.Assistant, "system" => ChatRole.System, _ => ChatRole.User },
        m.Text);

    private static string Title(string message)
    {
        var t = message.Trim().ReplaceLineEndings(" ");
        return t.Length == 0 ? "New chat" : t.Length > 60 ? t[..60].TrimEnd() + "…" : t;
    }

    private string CurrentRoute => (_chatClient as IRouteAware)?.CurrentRoute ?? _route;
}

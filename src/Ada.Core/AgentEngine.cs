using System.Runtime.CompilerServices;
using System.Text;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;

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
    private readonly ChatClientAgent _chatAgent;
    private readonly ChatClientAgent _toolAgent;
    private readonly bool _hasTools;
    private readonly int _toolCount;
    private readonly string _route;
    private readonly ITurnContext? _memory;
    private readonly ICompactionStrategy _compaction;
    private readonly IConversationStore? _conversations;
    private readonly ILogger<AgentEngine>? _log;
    private List<ChatMessage> _history = [];

    /// <summary>Ultimate backstop so a turn can never hang on "thinking" forever — if the agent (a stalled
    /// model stream, or a wedged tool/skill the per-tool cap somehow misses) produces nothing for this long,
    /// the turn ends with an error instead of spinning indefinitely. Generous: a local turn is normally seconds.</summary>
    private static readonly TimeSpan TurnTimeout = TimeSpan.FromSeconds(120);

    public AgentEngine(
        IChatClient chatClient,
        Persona persona,
        string route = "local",
        IEnumerable<AITool>? tools = null,
        ITurnContext? memory = null,
        ICompactionStrategy? compaction = null,
        AgentSkillsProvider? skills = null,
        IConversationStore? conversations = null,
        ILogger<AgentEngine>? log = null)
    {
        ArgumentNullException.ThrowIfNull(chatClient);
        ArgumentNullException.ThrowIfNull(persona);

        _chatClient = chatClient;
        _route = route;
        _memory = memory;
        _compaction = compaction ?? new NoCompaction();
        _conversations = conversations;
        _log = log;

        var toolList = tools?.ToList() ?? [];
        _toolCount = toolList.Count;
        _hasTools = _toolCount > 0 || skills is not null;
        _chatAgent = AdaAgentBuilder.Build(chatClient, persona.Instructions, [], skills: null);
        // Tool mode intentionally exposes only MAF skill-discovery tools. Direct tool schemas, even a
        // small set, make local Ollama reasoning models terminate early before loading a skill.
        _toolAgent = AdaAgentBuilder.Build(chatClient, persona.Instructions, [], skills);
    }

    /// <summary>The running history length — exposed so compaction behaviour is observable in tests.</summary>
    public int HistoryMessageCount => _history.Count;

    public async IAsyncEnumerable<AdaResponseChunk> RespondAsync(
        AdaRequest request, [EnumeratorCancellation] CancellationToken ct = default)
    {
        if (TryHandleWorkspaceFastPath(request.Message, out var fastReply))
        {
            yield return new AdaResponseChunk(fastReply, CurrentRoute);
            yield return new AdaResponseChunk(string.Empty, CurrentRoute, IsFinal: true);
            yield break;
        }

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
        var useTools = !request.ChatOnly && ShouldUseTools(request.Message);

        // M10 talker/thinker split: a voice turn that would enter the slow tool path is handed to
        // the background thinker instead — the tool path (load_skill + sandbox scripts) is exactly
        // what stalls voice for 10-30 s. The Delegate chunk carries the goal; a short acknowledgment
        // is what gets spoken. Text surfaces never set AllowDelegation, so their inline flow is
        // untouched (M10-T7).
        if (useTools && request.AllowDelegation)
        {
            yield return new AdaResponseChunk(
                string.Empty, CurrentRoute, Kind: AdaResponseChunkKind.Delegate,
                Goal: request.Message);

            const string ack = "On it — I'll work on that in the background and let you know what I find.";
            assistant.Append(ack);
            yield return new AdaResponseChunk(ack, CurrentRoute);
            yield return new AdaResponseChunk(string.Empty, CurrentRoute, IsFinal: true);

            PersistTurn(threaded, convo, request.Message, assistant.ToString());
            if (!threaded) await CompactHistoryAsync(ct).ConfigureAwait(false);
            yield break;
        }

        var agent = useTools ? _toolAgent : _chatAgent;
        _log?.LogInformation("[engine] selected {Mode} mode; toolsAvailable={Tools}", useTools ? "tools" : "chat", _toolCount);

        // Stream with a turn-level backstop: link a timer to the caller's token and enumerate manually, so a
        // stall anywhere in the agent (model stream, a wedged tool or skill) ends the turn with a message
        // instead of hanging on "thinking" forever.
        using var turnCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        turnCts.CancelAfter(TurnTimeout);
        var runOptions = useTools
            ? new ChatClientAgentRunOptions(new ChatOptions
            {
                ToolMode = ChatToolMode.RequireSpecific("load_skill"),
            })
            : null;
        var input = useTools ? ForceSkillChoice(turn, request.Message) : turn;
        var stream = agent.RunStreamingAsync(input, options: runOptions, cancellationToken: turnCts.Token).ConfigureAwait(false).GetAsyncEnumerator();
        try
        {
            while (true)
            {
                bool moved = false, timedOut = false;
                try { moved = await stream.MoveNextAsync(); }
                catch (OperationCanceledException) when (turnCts.IsCancellationRequested && !ct.IsCancellationRequested)
                {
                    timedOut = true; // yield can't live in a catch — surface it just below
                }
                if (timedOut)
                {
                    yield return new AdaResponseChunk(
                        assistant.Length == 0
                            ? "Sorry — I couldn't respond in time (a tool or the model seems stuck). Please try again."
                            : "\n\n…(stopped — the response stalled before finishing.)",
                        CurrentRoute);
                    break;
                }
                if (!moved) break;
                var update = stream.Current;
                if (update.FinishReason is not null)
                    _log?.LogInformation("[engine] stream finish: {Reason}; textSoFar={Text}", update.FinishReason, assistant.ToString());
                if (_log?.IsEnabled(LogLevel.Debug) == true)
                    _log.LogDebug("[engine] stream update: role={Role} finish={Finish} contents={Count} text='{Text}'",
                        update.Role, update.FinishReason, update.Contents.Count, update.Text);
                if (!string.IsNullOrEmpty(update.Text))
                {
                    assistant.Append(update.Text);
                    yield return new AdaResponseChunk(update.Text, CurrentRoute);
                }
            }
        }
        finally { await stream.DisposeAsync(); }
        yield return new AdaResponseChunk(string.Empty, CurrentRoute, IsFinal: true);

        PersistTurn(threaded, convo, request.Message, assistant.ToString());
        if (!threaded) await CompactHistoryAsync(ct).ConfigureAwait(false);
    }

    /// <summary>Record the finished turn: to the stored thread (threaded) or the in-process window.</summary>
    private void PersistTurn(bool threaded, Conversation? convo, string userText, string assistantText)
    {
        if (threaded)
        {
            // Persist the full turn — the stored thread keeps everything; only the model context was bounded.
            var ts = DateTimeOffset.UtcNow.ToString("o");
            convo!.Messages.Add(new ConversationMessage { Role = "user", Text = userText, Ts = ts });
            convo.Messages.Add(new ConversationMessage { Role = "assistant", Text = assistantText, Route = CurrentRoute, Ts = ts });
            _conversations!.Save(convo);
        }
        else
        {
            _history.Add(new ChatMessage(ChatRole.User, userText));
            _history.Add(new ChatMessage(ChatRole.Assistant, assistantText));
        }
    }

    /// <summary>Unthreaded: keep the in-process window bounded (existing behaviour).</summary>
    private async Task CompactHistoryAsync(CancellationToken ct)
        => _history = await _compaction.CompactAsync(_history, ct).ConfigureAwait(false);

    private static ChatMessage ToChatMessage(ConversationMessage m) => new(
        m.Role switch { "assistant" => ChatRole.Assistant, "system" => ChatRole.System, _ => ChatRole.User },
        m.Text);

    private static string Title(string message)
    {
        var t = message.Trim().ReplaceLineEndings(" ");
        return t.Length == 0 ? "New chat" : t.Length > 60 ? t[..60].TrimEnd() + "…" : t;
    }

    private string CurrentRoute => (_chatClient as IRouteAware)?.CurrentRoute ?? _route;

    private static bool TryHandleWorkspaceFastPath(string message, out string reply)
    {
        var s = message.ToLowerInvariant();
        if (ContainsAny(s, "list", "show") && ContainsAny(s, "workspace", "repo", "repository") && ContainsAny(s, "file", "files", "folder", "directory", "directories"))
        {
            reply = ListWorkspaceRoot();
            return true;
        }

        reply = string.Empty;
        return false;
    }

    private static string ListWorkspaceRoot()
    {
        var root = AdaPaths.EnsureWorkspaceDir();
        var entries = Directory.EnumerateFileSystemEntries(root)
            .OrderBy(p => Directory.Exists(p) ? 0 : 1)
            .ThenBy(Path.GetFileName, StringComparer.OrdinalIgnoreCase)
            .Select(p => Directory.Exists(p) ? Path.GetFileName(p) + "/" : Path.GetFileName(p))
            .Where(n => !string.IsNullOrWhiteSpace(n))
            .ToList();

        if (entries.Count == 0)
            return "Ada's workspace root is empty.";

        return "Ada's workspace root contains:\n" + string.Join("\n", entries.Select(e => "- " + e));
    }

    private static List<ChatMessage> ForceSkillChoice(List<ChatMessage> turn, string message)
    {
        var skill = SkillFor(message);
        var copy = new List<ChatMessage>(turn.Count + 1);
        copy.AddRange(turn);
        copy.Add(new ChatMessage(ChatRole.System,
            $"This action request requires a skill. First call load_skill with skillName='{skill}', then run the appropriate script and report the result."));
        return copy;
    }

    private static string SkillFor(string message)
    {
        var s = message.ToLowerInvariant();
        if (ContainsAny(s, "email", "mail", "outlook")) return "email-tools";
        if (ContainsAny(s, "schedule", "remind", "job", "calendar")) return "schedule-tools";
        if (ContainsAny(s, "remember", "recall", "forget")) return "memory-tools";
        if (ContainsAny(s, "http://", "https://", "web", "website", "browser", "url", "page", "search the web", "look up")) return "web-tools";
        if (ContainsAny(s, "test", "build", "code", "script", "dotnet", "python", "node", "npm")) return "code-tools";
        if (ContainsAny(s, "command", "terminal", "powershell", "shell", "docker", "container")) return "shell-tools";
        if (ContainsAny(s, "file", "folder", "directory", "workspace", "repo", "repository", "search", "find", "list", "read", "write", "delete", "create")) return "workspace-tools";
        return "ada-tools";
    }

    private bool ShouldUseTools(string message)
    {
        if (!_hasTools) return false;

        var s = message.ToLowerInvariant();
        return ContainsAny(s,
            "file", "folder", "directory", "workspace", "project", "repo", "repository",
            "read ", "write ", "list ", "delete ", "remove ", "create ", "edit ", "modify ", "save ",
            "run ", "execute ", "command", "terminal", "powershell", "shell", "code", "script", "tool", "tool call",
            "docker", "container", "sandbox", "browser", "web", "search", "look up", "website", "http://", "https://",
            "email", "mail", "outlook", "schedule", "remind", "remember", "recall", "forget",
            "install", "download", "upload", "open ", "launch ", "check ", "inspect ");
    }

    private static bool ContainsAny(string value, params string[] needles)
    {
        foreach (var needle in needles)
            if (value.Contains(needle, StringComparison.Ordinal)) return true;
        return false;
    }
}

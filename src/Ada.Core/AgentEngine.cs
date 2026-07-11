using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
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

    private const string FinalAnswerInstructions =
        "For the user-visible answer, write plain prose only. Do not include hidden reasoning, tool arguments, raw JSON, Markdown fences, backticks, or decorative symbols unless the user explicitly asks for them.";

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
        turn.Add(new ChatMessage(ChatRole.System, FinalAnswerInstructions));
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
            // Carry recent conversation so the thinker (its own history, no thread) can resolve
            // "that file" / pronouns / ellipses — otherwise it may do the wrong task (Codex #1).
            yield return new AdaResponseChunk(
                string.Empty, CurrentRoute, Kind: AdaResponseChunkKind.Delegate,
                Goal: request.Message, ContextSummary: RecentContext(turn));

            const string ack = "On it — I'll work on that in the background and let you know what I find.";
            assistant.Append(ack);
            yield return new AdaResponseChunk(ack, CurrentRoute);
            yield return new AdaResponseChunk(string.Empty, CurrentRoute, IsFinal: true);

            PersistTurn(threaded, convo, request.Message, assistant.ToString(), request.PersistUserMessage);
            if (!threaded) await CompactHistoryAsync(ct).ConfigureAwait(false);
            yield break;
        }

        var agent = useTools ? _toolAgent : _chatAgent;
        _log?.LogInformation("[engine] selected {Mode} mode; toolsAvailable={Tools}", useTools ? "tools" : "chat", _toolCount);
        var statusState = new StreamStatusState();
        var initialStatus = new AdaResponseChunk(
            useTools ? "Preparing tools" : "Thinking",
            CurrentRoute,
            Kind: useTools ? AdaResponseChunkKind.Tool : AdaResponseChunkKind.Thinking,
            Label: useTools ? "Tools" : "Thinking");
        statusState.LastKey = StatusKey(initialStatus.Kind, initialStatus.Text);
        yield return initialStatus;

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
        // Voice barge-in cancels the caller's token, which either throws out of MoveNextAsync or
        // disposes this iterator parked at a yield — both skip everything after the loop, so the
        // epilogue's PersistTurn never ran and the interrupted exchange vanished from history: the
        // next turn's context didn't know the user asked, or that Ada half-answered. The finally
        // persists the user message plus the truncated partial instead. (The partial is what was
        // GENERATED, which can outrun what was spoken — TTS lags the stream — so it's marked, an
        // honest approximation until spoken-prefix tracking exists.)
        var completed = false;
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
                var status = TryBuildStatusChunk(update, statusState, CurrentRoute);
                if (status is not null)
                    yield return status;
                if (!string.IsNullOrEmpty(update.Text))
                {
                    assistant.Append(update.Text);
                    yield return new AdaResponseChunk(update.Text, CurrentRoute);
                }
            }
            completed = true;
        }
        finally
        {
            await stream.DisposeAsync();
            // Only the caller-cancelled path (barge-in, session teardown) persists here — a model
            // error still leaves no trace, as before. Skip when there is nothing to record (no
            // partial text and a background-result delivery that must not write its user message).
            if (!completed && ct.IsCancellationRequested && (assistant.Length > 0 || request.PersistUserMessage))
                PersistTurn(threaded, convo, request.Message,
                    assistant.Length > 0 ? assistant.ToString() + " …(interrupted)" : string.Empty,
                    request.PersistUserMessage);
        }
        yield return new AdaResponseChunk(string.Empty, CurrentRoute, IsFinal: true);

        PersistTurn(threaded, convo, request.Message, assistant.ToString(), request.PersistUserMessage);
        if (!threaded) await CompactHistoryAsync(ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Record the turn — finished, or truncated by a barge-in (the interrupted partial arrives
    /// marked "…(interrupted)") — to the stored thread (threaded) or the in-process window. When
    /// <paramref name="persistUserMessage"/> is false (a background-result delivery, Codex #2), the
    /// synthetic "[System note…]" prompt is NOT written — only the spoken reply is recorded, and only
    /// if there is one (a result the relevance gate dropped to silence leaves no trace at all).
    /// </summary>
    private void PersistTurn(bool threaded, Conversation? convo, string userText, string assistantText, bool persistUserMessage)
    {
        if (threaded)
        {
            if (!persistUserMessage && string.IsNullOrWhiteSpace(assistantText))
                return; // silent background result — nothing to record, don't even re-save
            var ts = DateTimeOffset.UtcNow.ToString("o");
            if (persistUserMessage)
                convo!.Messages.Add(new ConversationMessage { Role = "user", Text = userText, Ts = ts });
            // No empty assistant records — an interrupted-before-any-text turn stores just the user side.
            if (!string.IsNullOrWhiteSpace(assistantText))
                convo!.Messages.Add(new ConversationMessage { Role = "assistant", Text = assistantText, Route = CurrentRoute, Ts = ts });
            _conversations!.Save(convo);
        }
        else
        {
            if (persistUserMessage)
                _history.Add(new ChatMessage(ChatRole.User, userText));
            if (!string.IsNullOrWhiteSpace(assistantText))
                _history.Add(new ChatMessage(ChatRole.Assistant, assistantText));
        }
    }

    /// <summary>A compact render of the last few conversation turns for a delegated task's context
    /// (Codex #1). Excludes system/memory messages and the current user message; caps length so a
    /// long history can't bloat the delegated request.</summary>
    private static string? RecentContext(List<ChatMessage> turn)
    {
        // turn = [optional system memory] + history + current user message. Drop the trailing user
        // message and any system messages, take the last few exchanges.
        var body = turn.Take(Math.Max(0, turn.Count - 1))
            .Where(m => m.Role == ChatRole.User || m.Role == ChatRole.Assistant)
            .TakeLast(6)
            .Select(m => $"{(m.Role == ChatRole.User ? "User" : "Ada")}: {m.Text}")
            .ToList();
        if (body.Count == 0) return null;
        var text = string.Join("\n", body);
        return text.Length > 1200 ? text[^1200..] : text; // keep it compact for a voice-latency turn
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

    private sealed class StreamStatusState
    {
        public string? LastKey { get; set; }
    }

    private static AdaResponseChunk? TryBuildStatusChunk(AgentResponseUpdate update, StreamStatusState state, string route)
    {
        foreach (var content in update.Contents)
        {
            var candidate = content switch
            {
                TextReasoningContent => (Kind: AdaResponseChunkKind.Thinking, Text: "Thinking", Label: "Thinking"),
                FunctionCallContent call => ToolCallStatus(call),
                ToolCallContent => (Kind: AdaResponseChunkKind.Tool, Text: "Calling tool", Label: "Tool"),
                ToolResultContent => (Kind: AdaResponseChunkKind.Tool, Text: "Reading tool result", Label: "Tool"),
                ToolApprovalRequestContent => (Kind: AdaResponseChunkKind.Status, Text: "Waiting for approval", Label: "Approval"),
                _ => (Kind: AdaResponseChunkKind.Answer, Text: string.Empty, Label: string.Empty),
            };

            if (candidate.Kind == AdaResponseChunkKind.Answer) continue;
            var key = StatusKey(candidate.Kind, candidate.Text);
            if (key == state.LastKey) continue;
            state.LastKey = key;
            return new AdaResponseChunk(candidate.Text, route, Kind: candidate.Kind, Label: candidate.Label);
        }

        return null;
    }

    private static (AdaResponseChunkKind Kind, string Text, string Label) ToolCallStatus(FunctionCallContent call)
    {
        if (string.Equals(call.Name, "load_skill", StringComparison.Ordinal))
        {
            var skill = ArgumentText(call, "skillName");
            return (AdaResponseChunkKind.Delegation,
                string.IsNullOrWhiteSpace(skill) ? "Loading skill" : $"Loading {skill} skill",
                "Skill");
        }

        if (string.Equals(call.Name, "run_skill_script", StringComparison.Ordinal))
        {
            var script = ArgumentText(call, "scriptName") ?? ArgumentText(call, "name");
            return (AdaResponseChunkKind.Tool,
                string.IsNullOrWhiteSpace(script) ? "Running skill script" : $"Running {script}",
                "Tool");
        }

        if (call.Name.Contains("agent", StringComparison.OrdinalIgnoreCase) ||
            call.Name.Contains("delegate", StringComparison.OrdinalIgnoreCase) ||
            call.Name.Contains("handoff", StringComparison.OrdinalIgnoreCase))
            return (AdaResponseChunkKind.Delegation, $"Delegating to {FriendlyName(call.Name)}", "Agent");

        return (AdaResponseChunkKind.Tool, $"Calling {FriendlyName(call.Name)}", "Tool");
    }

    private static string? ArgumentText(FunctionCallContent call, string name)
    {
        if (call.Arguments is null || !call.Arguments.TryGetValue(name, out var value) || value is null)
            return null;

        return value switch
        {
            string s => s,
            JsonElement { ValueKind: JsonValueKind.String } e => e.GetString(),
            JsonElement { ValueKind: JsonValueKind.Number } e => e.GetRawText(),
            JsonElement { ValueKind: JsonValueKind.True } => "true",
            JsonElement { ValueKind: JsonValueKind.False } => "false",
            _ => value.ToString(),
        };
    }

    private static string FriendlyName(string name)
        => string.IsNullOrWhiteSpace(name) ? "tool" : name.Replace('_', ' ').Replace('-', ' ');

    private static string StatusKey(AdaResponseChunkKind kind, string text) => kind + ":" + text;

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

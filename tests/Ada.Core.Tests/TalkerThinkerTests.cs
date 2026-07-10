using Ada.Core;
using Microsoft.Extensions.AI;

namespace Ada.Core.Tests;

/// <summary>
/// M10 §3: the engine-level talker/thinker split. A voice turn (AllowDelegation) that would enter
/// the slow tool path yields a Delegate chunk + a spoken acknowledgment instead; text surfaces
/// (no flag) and result deliveries (ChatOnly) keep their existing flow. Plus the background
/// approval scope: gated tools fail fast when nobody is watching (M10-T6).
/// </summary>
public class TalkerThinkerTests
{
    private static AgentEngine EngineWithTools() => new(
        new StubChatClient(),
        new Persona("You are Ada."),
        tools: [AIFunctionFactory.Create(() => "ok", "noop_tool")]);

    private static async Task<List<AdaResponseChunk>> DrainAsync(AgentEngine engine, AdaRequest request)
    {
        var chunks = new List<AdaResponseChunk>();
        await foreach (var chunk in engine.RespondAsync(request))
            chunks.Add(chunk);
        return chunks;
    }

    [Fact]
    public async Task A_Tool_Turn_With_AllowDelegation_Hands_Off_And_Acknowledges()
    {
        var engine = EngineWithTools();
        var chunks = await DrainAsync(engine,
            new AdaRequest("search the web for the Artemis launch window", AllowDelegation: true));

        var delegated = Assert.Single(chunks, c => c.Kind == AdaResponseChunkKind.Delegate);
        Assert.Equal("search the web for the Artemis launch window", delegated.Goal);

        // The spoken part: an acknowledgment, not an answer — and the turn closes normally.
        Assert.Contains(chunks, c => c.Kind == AdaResponseChunkKind.Answer && c.Text.Contains("background"));
        Assert.Contains(chunks, c => c.IsFinal);

        // The ack persists to history so the next turn has context (M10-T2's thread shape).
        Assert.Equal(2, engine.HistoryMessageCount);
    }

    [Fact]
    public async Task Text_Surfaces_Never_Delegate()
    {
        // No AllowDelegation (the default) — the inline tool flow is untouched (M10-T7).
        var engine = EngineWithTools();
        var chunks = await DrainAsync(engine, new AdaRequest("search the web for something"));

        Assert.DoesNotContain(chunks, c => c.Kind == AdaResponseChunkKind.Delegate);
    }

    [Fact]
    public async Task ChatOnly_Forces_The_Fast_Path_Even_For_Tool_Keywords()
    {
        // A background-result delivery mentions arbitrary content ("check", "file", …) that would
        // keyword-match the tool heuristic — ChatOnly must keep it on the chat agent.
        var engine = EngineWithTools();
        var chunks = await DrainAsync(engine,
            new AdaRequest("[System note] the file search finished: 3 results.", AllowDelegation: true, ChatOnly: true));

        Assert.DoesNotContain(chunks, c => c.Kind == AdaResponseChunkKind.Delegate);
        Assert.Contains(chunks, c => c.Kind == AdaResponseChunkKind.Answer && !string.IsNullOrEmpty(c.Text));
    }

    [Fact]
    public async Task A_Plain_Chat_Turn_With_AllowDelegation_Answers_Normally()
    {
        var engine = EngineWithTools();
        var chunks = await DrainAsync(engine, new AdaRequest("how are you today", AllowDelegation: true));

        Assert.DoesNotContain(chunks, c => c.Kind == AdaResponseChunkKind.Delegate);
        // The stub streams word-by-word — assert on the assembled reply.
        var reply = string.Concat(chunks.Where(c => c.Kind == AdaResponseChunkKind.Answer).Select(c => c.Text));
        Assert.Contains("how are you today", reply);
    }

    // ── ToolContext background scope (M10-T6) ─────────────────────────────

    private sealed class RecordingApprovalHandler : IApprovalHandler
    {
        public int Calls;
        public Task<ApprovalDecision> RequestApprovalAsync(ApprovalRequest request, CancellationToken ct = default)
        {
            Calls++;
            return Task.FromResult(ApprovalDecision.Approve(ApprovalGrant.Session));
        }
    }

    private sealed class NullAudit : IAuditLog
    {
        public Task RecordAsync(AuditEntry entry, CancellationToken ct = default) => Task.CompletedTask;
        public IReadOnlyList<AuditEntry> Recent(int count = 50) => [];
    }

    private static ToolContext Context(RecordingApprovalHandler approval) =>
        new(new ScopePolicy([Path.GetTempPath()], [], []), approval, new NullAudit());

    private static ApprovalRequest Mutation() =>
        new("write_file", RiskTier.Low, "write a file", "C:/tmp/x.txt");

    [Fact]
    public async Task Background_Scope_Denies_Gated_Tools_Without_Prompting()
    {
        var approval = new RecordingApprovalHandler();
        var context = Context(approval);

        using (ToolContext.EnterBackgroundScope())
        {
            Assert.False(await context.GateAsync(Mutation()));
        }
        Assert.Equal(0, approval.Calls); // fail fast — the interactive handler is never consulted

        // Outside the scope the gate works exactly as before.
        Assert.True(await context.GateAsync(Mutation()));
        Assert.Equal(1, approval.Calls);
    }

    [Fact]
    public async Task A_Session_Grant_Still_Applies_Inside_The_Background_Scope()
    {
        var approval = new RecordingApprovalHandler();
        var context = Context(approval);

        Assert.True(await context.GateAsync(Mutation())); // grants for the session
        using (ToolContext.EnterBackgroundScope())
        {
            Assert.True(await context.GateAsync(Mutation())); // already approved — no re-prompt, no deny
        }
        Assert.Equal(1, approval.Calls);
    }
}

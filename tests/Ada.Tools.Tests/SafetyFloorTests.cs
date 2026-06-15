using Ada.Core;

namespace Ada.Tools.Tests;

/// <summary>The adversarial gate for the M2 safety floor: scoping, approval, session-grant scoping,
/// audit, and injection inertness.</summary>
public sealed class SafetyFloorTests : IDisposable
{
    private readonly string _root;     // an allowed write root
    private readonly string _outside;  // outside every allowed root
    private readonly ScopePolicy _scope;

    public SafetyFloorTests()
    {
        _root = Directory.CreateTempSubdirectory("ada_allowed_").FullName;
        _outside = Directory.CreateTempSubdirectory("ada_outside_").FullName;
        _scope = new ScopePolicy(
            allowedRoots: [_root],
            writeDeniedRoots: [],
            secretRoots: [Path.Combine(_root, "secrets")]);
    }

    public void Dispose()
    {
        TryDelete(_root);
        TryDelete(_outside);
    }

    private static void TryDelete(string dir) { try { Directory.Delete(dir, true); } catch { /* best effort */ } }

    // ---------- scope ----------

    [Fact]
    public void Path_traversal_out_of_root_is_blocked()
    {
        var escape = Path.Combine(_root, "..", Path.GetFileName(_outside), "x.txt");
        Assert.Throws<ScopeViolationException>(() => _scope.ResolveForWrite(escape));
    }

    [Fact]
    public void Write_outside_allowed_roots_is_blocked()
        => Assert.False(_scope.IsWriteAllowed(Path.Combine(_outside, "x.txt")));

    [Fact]
    public void Write_inside_allowed_root_is_permitted()
        => Assert.True(_scope.IsWriteAllowed(Path.Combine(_root, "x.txt")));

    [Fact]
    public void Secret_vault_is_unreadable()
        => Assert.False(_scope.IsReadAllowed(Path.Combine(_root, "secrets", "key.txt")));

    // ---------- approval gating ----------

    [Fact]
    public async Task Denied_write_never_touches_disk_and_is_audited()
    {
        var audit = new InMemoryAuditLog();
        var fs = new FileSystemTools(new ToolContext(_scope, new AutoApprovalHandler(approveMutations: false), audit));
        var target = Path.Combine(_root, "denied.txt");

        var result = await fs.WriteFile(target, "data");

        Assert.Contains("Denied", result);
        Assert.False(File.Exists(target));
        Assert.Contains(audit.Recent(), e => e is { Tool: "write_file", Outcome: "denied" });
    }

    [Fact]
    public async Task Approved_write_creates_the_file_and_is_audited()
    {
        var audit = new InMemoryAuditLog();
        var fs = new FileSystemTools(new ToolContext(_scope, new AutoApprovalHandler(approveMutations: true), audit));
        var target = Path.Combine(_root, "ok.txt");

        await fs.WriteFile(target, "hello");

        Assert.Equal("hello", await File.ReadAllTextAsync(target));
        Assert.Contains(audit.Recent(), e => e is { Tool: "write_file", Outcome: "executed" });
    }

    [Fact]
    public async Task Write_outside_root_is_blocked_even_when_the_approver_says_yes()
    {
        var fs = new FileSystemTools(new ToolContext(_scope, new AutoApprovalHandler(approveMutations: true), new InMemoryAuditLog()));
        var target = Path.Combine(_outside, "x.txt");

        var result = await fs.WriteFile(target, "data");

        Assert.Contains("Blocked", result);
        Assert.False(File.Exists(target));
    }

    // ---------- session-grant scoping ----------

    [Fact]
    public async Task Session_grant_skips_the_second_prompt_but_never_widens_scope()
    {
        var counter = new CountingApprovalHandler(ApprovalGrant.Session);
        var fs = new FileSystemTools(new ToolContext(_scope, counter, new InMemoryAuditLog()));

        await fs.WriteFile(Path.Combine(_root, "a.txt"), "1");
        await fs.WriteFile(Path.Combine(_root, "b.txt"), "2");
        Assert.Equal(1, counter.Prompts); // the second write rode the session grant — no new prompt

        var escaped = await fs.WriteFile(Path.Combine(_outside, "c.txt"), "3");
        Assert.Contains("Blocked", escaped);
        Assert.Equal(1, counter.Prompts); // scope blocked it before any prompt — the grant didn't widen reach
    }

    // ---------- injection inertness ----------

    [Fact]
    public async Task Injection_string_is_written_literally_not_executed()
    {
        var fs = new FileSystemTools(new ToolContext(_scope, new AutoApprovalHandler(approveMutations: true), new InMemoryAuditLog()));
        var payload = "\"; rm -rf / #";
        var target = Path.Combine(_root, "inj.txt");

        await fs.WriteFile(target, payload);

        Assert.Equal(payload, await File.ReadAllTextAsync(target));
    }

    // ---------- the card shows the literal target ----------

    [Fact]
    public async Task Approval_request_carries_the_literal_resolved_path()
    {
        ApprovalRequest? captured = null;
        var handler = new DelegatingApprovalHandler(r => { captured = r; return ApprovalDecision.Approve(); });
        var fs = new FileSystemTools(new ToolContext(_scope, handler, new InMemoryAuditLog()));
        var target = Path.Combine(_root, "card.txt");

        await fs.WriteFile(target, "x");

        Assert.NotNull(captured);
        Assert.Equal("write_file", captured!.Tool);
        Assert.EndsWith("card.txt", captured.Detail);
        Assert.StartsWith(_root, captured.Detail);
    }

    private sealed class DelegatingApprovalHandler(Func<ApprovalRequest, ApprovalDecision> fn) : IApprovalHandler
    {
        public Task<ApprovalDecision> RequestApprovalAsync(ApprovalRequest request, CancellationToken ct = default)
            => Task.FromResult(request.Tier == RiskTier.ReadOnly ? ApprovalDecision.Approve() : fn(request));
    }

    private sealed class CountingApprovalHandler(ApprovalGrant grant) : IApprovalHandler
    {
        public int Prompts { get; private set; }

        public Task<ApprovalDecision> RequestApprovalAsync(ApprovalRequest request, CancellationToken ct = default)
        {
            if (request.Tier == RiskTier.ReadOnly) return Task.FromResult(ApprovalDecision.Approve());
            Prompts++;
            return Task.FromResult(ApprovalDecision.Approve(grant));
        }
    }
}

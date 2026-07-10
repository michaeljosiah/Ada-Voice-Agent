namespace Ada.Core;

/// <summary>
/// Bundles the harness every Ada tool needs — scoping, approval, audit — and enforces the gate:
/// ReadOnly passes freely, mutations need approval, and "approve for session" is remembered per tool
/// for this process only. Scope is checked by the tools themselves before the gate, so a session
/// grant can never widen the blast radius.
/// </summary>
public sealed class ToolContext(IScopePolicy scope, IApprovalHandler approval, IAuditLog audit)
{
    private static readonly AsyncLocal<bool> BackgroundScope = new();

    private readonly HashSet<string> _sessionGrants = new();
    private readonly Lock _lock = new();

    public IScopePolicy Scope { get; } = scope;
    public IApprovalHandler Approval { get; } = approval;
    public IAuditLog Audit { get; } = audit;

    /// <summary>
    /// M10: marks the current async flow as a background (thinker) turn — nobody is watching an
    /// approval card for work they delegated and moved past, so the gate fails fast instead of
    /// hanging on the interactive handler. Session grants already given still apply; ReadOnly
    /// tools never reach the gate. Flows through MAF tool invocation via AsyncLocal.
    /// </summary>
    public static IDisposable EnterBackgroundScope()
    {
        BackgroundScope.Value = true;
        return new BackgroundScopeExit();
    }

    private sealed class BackgroundScopeExit : IDisposable
    {
        public void Dispose() => BackgroundScope.Value = false;
    }

    /// <summary>Runs the approval gate for a mutating action. Returns true to proceed. A session grant
    /// short-circuits the prompt for the same tool; scope is still enforced by the caller.</summary>
    public async Task<bool> GateAsync(ApprovalRequest request, CancellationToken ct = default)
    {
        lock (_lock)
        {
            if (_sessionGrants.Contains(request.Tool))
                return true;
        }

        // Background turns never pop interactive approval (M10-T6): deny fast so the tool reports
        // "needs your approval" into the thinker's result instead of stranding the task.
        if (BackgroundScope.Value)
            return false;

        var decision = await Approval.RequestApprovalAsync(request, ct).ConfigureAwait(false);
        if (decision.Approved && decision.Grant == ApprovalGrant.Session)
            lock (_lock) { _sessionGrants.Add(request.Tool); }

        return decision.Approved;
    }
}

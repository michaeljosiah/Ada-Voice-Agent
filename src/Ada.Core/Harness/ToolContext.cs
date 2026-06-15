namespace Ada.Core;

/// <summary>
/// Bundles the harness every Ada tool needs — scoping, approval, audit — and enforces the gate:
/// ReadOnly passes freely, mutations need approval, and "approve for session" is remembered per tool
/// for this process only. Scope is checked by the tools themselves before the gate, so a session
/// grant can never widen the blast radius.
/// </summary>
public sealed class ToolContext(IScopePolicy scope, IApprovalHandler approval, IAuditLog audit)
{
    private readonly HashSet<string> _sessionGrants = new();
    private readonly Lock _lock = new();

    public IScopePolicy Scope { get; } = scope;
    public IApprovalHandler Approval { get; } = approval;
    public IAuditLog Audit { get; } = audit;

    /// <summary>Runs the approval gate for a mutating action. Returns true to proceed. A session grant
    /// short-circuits the prompt for the same tool; scope is still enforced by the caller.</summary>
    public async Task<bool> GateAsync(ApprovalRequest request, CancellationToken ct = default)
    {
        lock (_lock)
        {
            if (_sessionGrants.Contains(request.Tool))
                return true;
        }

        var decision = await Approval.RequestApprovalAsync(request, ct).ConfigureAwait(false);
        if (decision.Approved && decision.Grant == ApprovalGrant.Session)
            lock (_lock) { _sessionGrants.Add(request.Tool); }

        return decision.Approved;
    }
}

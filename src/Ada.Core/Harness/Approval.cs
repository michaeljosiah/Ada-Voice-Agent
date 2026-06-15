namespace Ada.Core;

public enum ApprovalGrant { Once, Session }

/// <summary>
/// What Ada is asking permission to do. It carries the <see cref="Detail"/> — the literal command or
/// path — so the approval card shows exactly what will happen, never a vague paraphrase (spec §8.1).
/// </summary>
public sealed record ApprovalRequest(string Tool, RiskTier Tier, string Summary, string Detail)
{
    public string Id { get; init; } = Guid.NewGuid().ToString("n");
}

public sealed record ApprovalDecision(bool Approved, ApprovalGrant Grant = ApprovalGrant.Once)
{
    public static readonly ApprovalDecision Denied = new(false);
    public static ApprovalDecision Approve(ApprovalGrant grant = ApprovalGrant.Once) => new(true, grant);
}

public interface IApprovalHandler
{
    Task<ApprovalDecision> RequestApprovalAsync(ApprovalRequest request, CancellationToken ct = default);
}

/// <summary>Headless approver for the CLI and tests: ReadOnly always passes; mutations follow a fixed
/// policy (approve-all or deny-all), optionally granting for the whole session.</summary>
public sealed class AutoApprovalHandler(bool approveMutations, ApprovalGrant grant = ApprovalGrant.Once) : IApprovalHandler
{
    public Task<ApprovalDecision> RequestApprovalAsync(ApprovalRequest request, CancellationToken ct = default)
        => Task.FromResult(request.Tier == RiskTier.ReadOnly || approveMutations
            ? ApprovalDecision.Approve(grant)
            : ApprovalDecision.Denied);
}

/// <summary>The safe default when no interactive UI is wired: allow reads, deny every mutation.</summary>
public sealed class DenyAllApprovalHandler : IApprovalHandler
{
    public Task<ApprovalDecision> RequestApprovalAsync(ApprovalRequest request, CancellationToken ct = default)
        => Task.FromResult(request.Tier == RiskTier.ReadOnly ? ApprovalDecision.Approve() : ApprovalDecision.Denied);
}

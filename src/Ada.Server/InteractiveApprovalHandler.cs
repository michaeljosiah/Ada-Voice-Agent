using System.Collections.Concurrent;
using Ada.Core;

namespace Ada.Server;

/// <summary>
/// The approval handler that surfaces each mutating request to the WebView2 UI as a card and waits
/// for the user's click. Pending requests are observable so the server can push them over SSE; the
/// decision endpoint resolves them. ReadOnly never reaches here.
/// </summary>
public sealed class InteractiveApprovalHandler : IApprovalHandler
{
    private readonly ConcurrentDictionary<string, Pending> _pending = new();

    /// <summary>Raised when a new approval is needed — the SSE endpoint forwards it to the UI.</summary>
    public event Action<ApprovalRequest>? Requested;

    public Task<ApprovalDecision> RequestApprovalAsync(ApprovalRequest request, CancellationToken ct = default)
    {
        if (request.Tier == RiskTier.ReadOnly)
            return Task.FromResult(ApprovalDecision.Approve());

        var tcs = new TaskCompletionSource<ApprovalDecision>(TaskCreationOptions.RunContinuationsAsynchronously);
        _pending[request.Id] = new Pending(request, tcs);
        ct.Register(() => Resolve(request.Id, ApprovalDecision.Denied));
        Requested?.Invoke(request);
        return tcs.Task;
    }

    public IReadOnlyCollection<ApprovalRequest> Pending_ => _pending.Values.Select(p => p.Request).ToArray();

    /// <summary>Completes a pending approval with the user's decision. Returns false if unknown/expired.</summary>
    public bool Resolve(string id, ApprovalDecision decision)
    {
        if (_pending.TryRemove(id, out var pending))
        {
            pending.Tcs.TrySetResult(decision);
            return true;
        }
        return false;
    }

    private sealed record Pending(ApprovalRequest Request, TaskCompletionSource<ApprovalDecision> Tcs);
}

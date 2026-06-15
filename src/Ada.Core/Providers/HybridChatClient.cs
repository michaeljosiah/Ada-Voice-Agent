using Microsoft.Extensions.AI;

namespace Ada.Core;

/// <summary>
/// The hybrid router as a delegating <see cref="IChatClient"/> (spec §6.5). Each turn is routed to
/// the default (local) or escalation (cloud) backend by task shape. The chosen route is exposed for
/// the UI badge, and every cloud use is recorded as <em>egress</em> in the audit log — escalation is
/// always visible, never silent.
/// </summary>
public sealed class HybridChatClient(IChatClient defaultClient, IChatClient? escalation, IRoutingPolicy policy, IAuditLog? audit = null)
    : IChatClient, IRouteAware
{
    public string CurrentRoute { get; private set; } = "local";

    public Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
    {
        var list = AsList(messages);
        return Pick(list, options).GetResponseAsync(list, options, cancellationToken);
    }

    public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
    {
        var list = AsList(messages);
        return Pick(list, options).GetStreamingResponseAsync(list, options, cancellationToken);
    }

    public object? GetService(Type serviceType, object? serviceKey = null) => defaultClient.GetService(serviceType, serviceKey);

    public void Dispose() { defaultClient.Dispose(); escalation?.Dispose(); }

    private IChatClient Pick(IList<ChatMessage> messages, ChatOptions? options)
    {
        var decision = policy.Route(messages, options);
        var useCloud = decision.Role == ModelRole.Escalation && escalation is not null;

        CurrentRoute = useCloud ? decision.Label : "local";
        if (useCloud)
            _ = audit?.RecordAsync(new AuditEntry("model_egress", decision.Label, RiskTier.Medium, "escalated", decision.Reason));

        return useCloud ? escalation! : defaultClient;
    }

    private static IList<ChatMessage> AsList(IEnumerable<ChatMessage> messages)
        => messages as IList<ChatMessage> ?? messages.ToList();
}

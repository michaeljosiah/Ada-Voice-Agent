using Microsoft.Extensions.AI;

namespace Ada.Core;

public sealed record RouteDecision(ModelRole Role, string Label, string Reason);

public interface IRoutingPolicy
{
    RouteDecision Route(IList<ChatMessage> messages, ChatOptions? options);
}

/// <summary>
/// Escalate on task shape, not on vibes (spec §6.6): long context, a code task, or a clearly
/// multi-step/analytical ask goes to the escalation (cloud) model; everything else stays local. The
/// "stay local" override pins every turn to the default model.
/// </summary>
public sealed class RoutingPolicy(bool hasEscalation, bool stayLocal, string localLabel = "local", string escalationLabel = "cloud")
    : IRoutingPolicy
{
    private static readonly string[] CodeSignals =
        ["```", "refactor", "implement", "debug", "stack trace", "function ", "class ", "def ", "compile", "algorithm", "regex"];

    private static readonly string[] HeavySignals =
        ["step by step", "step-by-step", "make a plan", "analyze", "research", "compare ", "draft a", "write a long"];

    public RouteDecision Route(IList<ChatMessage> messages, ChatOptions? options)
    {
        if (!hasEscalation) return new(ModelRole.Default, localLabel, "no cloud provider connected");
        if (stayLocal) return new(ModelRole.Default, localLabel, "stay-local override");

        var text = messages.LastOrDefault(m => m.Role == ChatRole.User)?.Text ?? string.Empty;
        var lower = text.ToLowerInvariant();
        var totalChars = messages.Sum(m => m.Text?.Length ?? 0);

        if (totalChars > 6000) return Escalate("long context");
        if (CodeSignals.Any(lower.Contains)) return Escalate("code task");
        if (HeavySignals.Any(lower.Contains)) return Escalate("multi-step task");
        return new(ModelRole.Default, localLabel, "simple turn");
    }

    private RouteDecision Escalate(string reason) => new(ModelRole.Escalation, $"{escalationLabel} · {reason}", reason);
}

using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;

namespace Ada.Core;

/// <summary>
/// Builds the <see cref="AIAgent"/> Ada presents to the voice pipeline (M6). It carries the same
/// persona, enabled skills, tools, and model routing as the text engine — but as a stateless agent
/// Voxa drives per connection (Voxa manages the voice conversation's turn state itself).
/// </summary>
public static class AdaAgentFactory
{
    public static AIAgent Create(IServiceProvider sp)
    {
        var options = sp.GetService<AdaModelOptions>() ?? AdaModelOptions.FromEnvironment();
        var registry = sp.GetRequiredService<ProviderRegistry>();
        var skills = sp.GetService<SkillRegistry>();
        var audit = sp.GetService<IAuditLog>();
        var composed = SkillComposer.Compose(sp.GetRequiredService<Persona>(), sp.GetServices<AITool>(), skills?.Enabled ?? []);

        var local = registry.CreateForRole(ModelRole.Default) ?? ModelClientFactory.Create(options);
        var cloud = registry.CreateForRole(ModelRole.Escalation);

        IChatClient client = (local, cloud) switch
        {
            (not null, not null) => new HybridChatClient(local, cloud,
                new RoutingPolicy(hasEscalation: true,
                    stayLocal: string.Equals(Environment.GetEnvironmentVariable("ADA_STAY_LOCAL"), "1", StringComparison.Ordinal),
                    localLabel: "local", escalationLabel: registry.ForRole(ModelRole.Escalation)!.Id),
                audit),
            (not null, null) => local,
            (null, not null) => cloud,
            _ => new StubChatClient(),
        };

        // AIO-first: when the sandbox is up, swap the host fs/shell tools for its own (briefly waiting
        // for the background bring-up to settle so the voice agent starts with the right tools).
        var sandbox = sp.GetService<SandboxSession>();
        sandbox?.WaitUntilReady(TimeSpan.FromSeconds(20));
        var tools = sandbox?.ApplyTo(composed.Tools) ?? composed.Tools;

        // File-based skills (MAF), with their bundled scripts run in the sandbox. Attached only when
        // at least one skill exists, so the skill tools stay off the surface otherwise.
        var skillsProvider = sandbox is not null ? AdaSkills.BuildProvider(sandbox) : null;
        return AdaAgentBuilder.Build(client, composed.Instructions, tools, skillsProvider);
    }
}

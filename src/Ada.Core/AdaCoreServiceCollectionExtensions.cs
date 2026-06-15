using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Ada.Core;

public static class AdaCoreServiceCollectionExtensions
{
    /// <summary>
    /// Registers Ada's core services and builds the engine from configuration: a hybrid router over a
    /// local default and a cloud escalation provider when both are configured; a single client when
    /// only one is; the offline echo brain when none is — degraded, never broken.
    /// </summary>
    public static IServiceCollection AddAdaCore(this IServiceCollection services, AdaModelOptions? options = null)
    {
        options ??= AdaModelOptions.FromEnvironment();
        services.AddSingleton(options);
        services.AddSingleton(_ => Persona.Load());
        services.TryAddSingleton<ICredentialVault>(_ => new DpapiCredentialVault());
        services.TryAddSingleton(_ => new ProviderStore());
        services.AddSingleton(sp => new ProviderRegistry(sp.GetRequiredService<ProviderStore>(), sp.GetRequiredService<ICredentialVault>()));

        services.AddSingleton<IAdaEngine>(sp => BuildEngine(sp, options));
        return services;
    }

    /// <summary>Wires the agent engine over an explicit chat client (tests, the self-test stub).</summary>
    public static IServiceCollection AddAdaCore(this IServiceCollection services, IChatClient chatClient, string route = "local")
    {
        services.AddSingleton(chatClient);
        services.AddSingleton(_ => Persona.Load());
        services.AddSingleton<IAdaEngine>(sp => new AgentEngine(chatClient, sp.GetRequiredService<Persona>(), route, sp.GetServices<AITool>()));
        return services;
    }

    private static IAdaEngine BuildEngine(IServiceProvider sp, AdaModelOptions options)
    {
        var registry = sp.GetRequiredService<ProviderRegistry>();
        var persona = sp.GetRequiredService<Persona>();
        var tools = sp.GetServices<AITool>();
        var audit = sp.GetService<IAuditLog>();

        // Default (local) brain: a configured Default provider, else the env/M1 local model.
        var local = registry.CreateForRole(ModelRole.Default) ?? ModelClientFactory.Create(options);
        var cloud = registry.CreateForRole(ModelRole.Escalation);

        if (local is null && cloud is null)
            return new EchoEngine();

        if (local is not null && cloud is not null)
        {
            var stayLocal = string.Equals(Environment.GetEnvironmentVariable("ADA_STAY_LOCAL"), "1", StringComparison.Ordinal);
            var escalationId = registry.ForRole(ModelRole.Escalation)!.Id;
            var policy = new RoutingPolicy(hasEscalation: true, stayLocal, localLabel: "local", escalationLabel: escalationId);
            var hybrid = new HybridChatClient(local, cloud, policy, audit);
            return new AgentEngine(hybrid, persona, tools: tools);
        }

        var single = local ?? cloud!;
        var route = local is not null ? "local" : registry.ForRole(ModelRole.Escalation)!.Id;
        return new AgentEngine(single, persona, route, tools);
    }
}

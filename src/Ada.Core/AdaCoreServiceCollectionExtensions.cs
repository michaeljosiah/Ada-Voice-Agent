using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;

namespace Ada.Core;

public static class AdaCoreServiceCollectionExtensions
{
    /// <summary>
    /// Registers Ada's core services, choosing the engine from configuration. With a local model
    /// configured (<c>ADA_PROVIDER=openai-compatible</c> …) Ada runs the real Agent Framework
    /// engine; otherwise she falls back to the offline echo brain — degraded, never broken.
    /// </summary>
    public static IServiceCollection AddAdaCore(this IServiceCollection services, AdaModelOptions? options = null)
    {
        options ??= AdaModelOptions.FromEnvironment();
        services.AddSingleton(options);
        services.AddSingleton(_ => Persona.Load());

        var chatClient = ModelClientFactory.Create(options);
        if (chatClient is not null)
        {
            services.AddSingleton(chatClient);
            var route = options.Provider == "foundry-local" ? "local · foundry" : "local";
            services.AddSingleton<IAdaEngine>(sp =>
                new AgentEngine(sp.GetRequiredService<IChatClient>(), sp.GetRequiredService<Persona>(), route));
        }
        else
        {
            services.AddSingleton<IAdaEngine, EchoEngine>();
        }

        return services;
    }

    /// <summary>Wires the agent engine over an explicit chat client (tests, the self-test stub).</summary>
    public static IServiceCollection AddAdaCore(this IServiceCollection services, IChatClient chatClient, string route = "local")
    {
        services.AddSingleton(chatClient);
        services.AddSingleton(_ => Persona.Load());
        services.AddSingleton<IAdaEngine>(sp => new AgentEngine(chatClient, sp.GetRequiredService<Persona>(), route));
        return services;
    }
}

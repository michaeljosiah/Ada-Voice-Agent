using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Ada.Core;

public static class AdaCoreServiceCollectionExtensions
{
    /// <summary>
    /// Registers Ada's core services and builds the engine from configuration: a hybrid router over a
    /// local default and a cloud escalation provider when both are configured; a single client when
    /// only one is; the offline echo brain when none is. Memory and compaction are wired in too.
    /// </summary>
    public static IServiceCollection AddAdaCore(this IServiceCollection services, AdaModelOptions? options = null)
    {
        options ??= AdaModelOptions.FromEnvironment();

        // Choose the local brain (unless the env set a provider explicitly). Default after setup is the
        // managed Ollama runtime (consumed via its OpenAI-compatible surface); ONNX in-process is opt-in.
        if (options.Provider == "echo")
        {
            var cfg = new ConfigStore().Load();
            if (cfg.LocalRuntime == "ollama")
            {
                options = new AdaModelOptions { Provider = "openai-compatible", Endpoint = "http://127.0.0.1:11434/v1", ModelId = cfg.OllamaModel ?? "gemma3:4b" };
            }
            else
            {
                var store = new OnnxModelStore();
                var modelId = cfg.LocalModelId ?? store.Downloaded().FirstOrDefault();
                if (modelId is not null && store.IsReady(modelId))
                    options = new AdaModelOptions { Provider = "onnx", ModelId = modelId };
            }
        }

        services.AddSingleton(options);
        services.AddSingleton(_ => Persona.Load());
        services.TryAddSingleton<ICredentialVault>(_ => new DpapiCredentialVault());
        services.TryAddSingleton(_ => new ProviderStore());
        services.TryAddSingleton(_ => new ConfigStore());
        services.AddSingleton(sp => new ProviderRegistry(sp.GetRequiredService<ProviderStore>(), sp.GetRequiredService<ICredentialVault>()));

        services.TryAddSingleton<IMemoryStore>(_ => new FileMemoryStore());
        services.TryAddSingleton(_ => new UserModel());
        services.TryAddSingleton<ITurnContext>(sp => new MemoryContextProvider(sp.GetRequiredService<IMemoryStore>(), sp.GetRequiredService<UserModel>()));
        services.TryAddSingleton(BuildCompaction);
        services.TryAddSingleton<SandboxSession>(); // the live work environment (AIO sandbox or host fallback)

        services.AddSingleton<IAdaEngine>(sp => BuildEngine(sp, options));
        services.TryAddSingleton<AIAgent>(AdaAgentFactory.Create); // the agent the voice plane drives
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

    private static ICompactionStrategy BuildCompaction(IServiceProvider sp)
    {
        var summarizer = sp.GetRequiredService<ProviderRegistry>().CreateForRole(ModelRole.Summarizer);
        return summarizer is not null
            ? new LengthCompactionStrategy(summarize: ModelSummarizer.For(summarizer))
            : new LengthCompactionStrategy();
    }

    private static IAdaEngine BuildEngine(IServiceProvider sp, AdaModelOptions options)
    {
        var registry = sp.GetRequiredService<ProviderRegistry>();
        var audit = sp.GetService<IAuditLog>();
        var memory = sp.GetService<ITurnContext>();
        var compaction = sp.GetService<ICompactionStrategy>();

        // Compose the persona + enabled skills' instructions and tools (no core change to add a skill).
        var skillRegistry = sp.GetService<SkillRegistry>();
        var composed = SkillComposer.Compose(sp.GetRequiredService<Persona>(), sp.GetServices<AITool>(), skillRegistry?.Enabled ?? []);
        var persona = new Persona(composed.Instructions);

        // AIO-first: when the sandbox is up, swap the host fs/shell tools for its own. Wait briefly for
        // the background bring-up to settle so the first turn already has the right tools.
        var sandbox = sp.GetService<SandboxSession>();
        sandbox?.WaitUntilReady(TimeSpan.FromSeconds(20));
        var tools = sandbox?.ApplyTo(composed.Tools) ?? composed.Tools;

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
            return new AgentEngine(hybrid, persona, tools: tools, memory: memory, compaction: compaction);
        }

        var single = local ?? cloud!;
        var route = local is not null ? "local" : registry.ForRole(ModelRole.Escalation)!.Id;
        return new AgentEngine(single, persona, route, tools, memory, compaction);
    }
}

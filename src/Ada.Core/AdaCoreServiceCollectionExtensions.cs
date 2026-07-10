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
        services.TryAddSingleton<ImageProvisioner>(); // downloads/keeps the sandbox + run_code Docker images
        services.TryAddSingleton<IConversationStore>(_ => new FileConversationStore()); // durable per-thread history

        services.AddSingleton<IAdaEngine>(sp => BuildEngine(sp, options));
        // M10: the background "thinker" — an engine on the heavyweight route with the full tool
        // harness and its OWN in-process history (never the talker's, never a stored thread), so
        // delegated research can't pollute the conversation. TRANSIENT (Codex #4): the background
        // processor runs tasks concurrently, so each delegated task gets a FRESH engine — one shared
        // instance would race on its mutable history and bleed research context across tasks and
        // sessions. The driver resolves one per task via a factory; construction is cheap (services
        // already resolved) so per-task cost is negligible.
        services.AddKeyedTransient<IAdaEngine>(ThinkerEngineKey, (sp, _) => BuildThinkerEngine(sp, options));
        services.TryAddSingleton<AIAgent>(AdaAgentFactory.Create); // the agent the voice plane drives
        return services;
    }

    /// <summary>Keyed-service key for the M10 background thinker engine.</summary>
    public const string ThinkerEngineKey = "ada:thinker";

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
        var tools = sandbox?.DirectToolsFor(composed.Tools) ?? composed.Tools;

        // MAF progressive-discovery skills: file-based skills plus mounted sandbox tools as skill scripts.
        var skills = sandbox is not null
            ? AdaSkills.BuildProvider(sandbox, tools, sandbox.Active ? sandbox.Tools : null, sp.GetService<Microsoft.Extensions.Logging.ILoggerFactory>())
            : null;
        var conversations = sp.GetService<IConversationStore>(); // durable per-thread history

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
            return new AgentEngine(hybrid, persona, tools: tools, memory: memory, compaction: compaction, skills: skills, conversations: conversations, log: sp.GetService<Microsoft.Extensions.Logging.ILogger<AgentEngine>>());
        }

        var single = local ?? cloud!;
        var route = local is not null ? "local" : registry.ForRole(ModelRole.Escalation)!.Id;
        return new AgentEngine(single, persona, route, tools, memory, compaction, skills, conversations, sp.GetService<Microsoft.Extensions.Logging.ILogger<AgentEngine>>());
    }

    /// <summary>
    /// M10: the thinker takes the heavyweight route directly — no hybrid router, no heuristics;
    /// delegation IS the escalation decision. Falls back to the local client (still useful: the
    /// tool path off the voice-latency budget) and finally to echo when nothing is configured.
    /// Same persona/tools/skills composition as the talker, plus researcher framing; no
    /// conversation store — results flow back through the voice turn, not a thread.
    /// </summary>
    private static IAdaEngine BuildThinkerEngine(IServiceProvider sp, AdaModelOptions options)
    {
        var registry = sp.GetRequiredService<ProviderRegistry>();
        var memory = sp.GetService<ITurnContext>();
        var compaction = sp.GetService<ICompactionStrategy>();

        var skillRegistry = sp.GetService<SkillRegistry>();
        var composed = SkillComposer.Compose(sp.GetRequiredService<Persona>(), sp.GetServices<AITool>(), skillRegistry?.Enabled ?? []);
        var persona = new Persona(composed.Instructions +
            "\n\nYou are running as Ada's background researcher: another assistant is speaking with the user " +
            "while you work. Use your tools, then answer in 2-3 compact sentences — your answer is read " +
            "aloud, so no lists or markdown. If a tool reports it needs approval, say so briefly.");

        var sandbox = sp.GetService<SandboxSession>();
        sandbox?.WaitUntilReady(TimeSpan.FromSeconds(20));
        var tools = sandbox?.DirectToolsFor(composed.Tools) ?? composed.Tools;
        var skills = sandbox is not null
            ? AdaSkills.BuildProvider(sandbox, tools, sandbox.Active ? sandbox.Tools : null, sp.GetService<Microsoft.Extensions.Logging.ILoggerFactory>())
            : null;

        var cloud = registry.CreateForRole(ModelRole.Escalation);
        var local = registry.CreateForRole(ModelRole.Default) ?? ModelClientFactory.Create(options);
        var client = cloud ?? local;
        if (client is null) return new EchoEngine();

        var route = cloud is not null ? registry.ForRole(ModelRole.Escalation)!.Id : "local";
        return new AgentEngine(client, persona, route, tools, memory, compaction, skills,
            conversations: null, sp.GetService<Microsoft.Extensions.Logging.ILogger<AgentEngine>>());
    }
}

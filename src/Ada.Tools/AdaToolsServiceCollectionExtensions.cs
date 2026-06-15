using Ada.Core;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Ada.Tools;

public static class AdaToolsServiceCollectionExtensions
{
    /// <summary>
    /// Registers the harness defaults (scope, audit, approval, sandbox) and Ada's built-in tools as
    /// <see cref="AITool"/> services so the agent can pick them up. A host may register its own
    /// <see cref="IApprovalHandler"/> (e.g. the interactive one) <em>before</em> calling this; the
    /// <c>TryAdd</c> here respects it. Otherwise reads pass and mutations are denied by default.
    /// </summary>
    public static IServiceCollection AddAdaTools(this IServiceCollection services)
    {
        services.TryAddSingleton<IScopePolicy>(_ => ScopePolicy.Default());
        services.TryAddSingleton<IAuditLog>(_ => new FileAuditLog());
        services.TryAddSingleton<IApprovalHandler, DenyAllApprovalHandler>();
        services.TryAddSingleton<ToolContext>();
        services.TryAddSingleton<ICodeSandbox, WasmCodeSandbox>();

        services.TryAddSingleton<IMemoryStore>(_ => new FileMemoryStore());
        services.TryAddSingleton<FileSystemTools>();
        services.TryAddSingleton<ShellTools>();
        services.TryAddSingleton<MemoryTools>();
        services.TryAddSingleton<WebTools>();

        // Skills (spec §7.3) and the MCP mounter (§7.4).
        services.AddSingleton<ISkill, ResearchSkill>();
        services.AddSingleton<ISkill, DesktopSkill>();
        services.AddSingleton<ISkill, FinanceRecordsSkill>();
        services.TryAddSingleton(sp => new SkillRegistry(sp.GetServices<ISkill>()));
        services.TryAddSingleton(sp => new McpMounter(sp.GetRequiredService<IApprovalHandler>(), sp.GetRequiredService<IAuditLog>()));

        services.AddSingleton<AITool>(sp => AIFunctionFactory.Create(sp.GetRequiredService<FileSystemTools>().ReadFile, "read_file"));
        services.AddSingleton<AITool>(sp => AIFunctionFactory.Create(sp.GetRequiredService<FileSystemTools>().ListDirectory, "list_directory"));
        services.AddSingleton<AITool>(sp => AIFunctionFactory.Create(sp.GetRequiredService<FileSystemTools>().WriteFile, "write_file"));
        services.AddSingleton<AITool>(sp => AIFunctionFactory.Create(sp.GetRequiredService<FileSystemTools>().DeleteFile, "delete_file"));
        services.AddSingleton<AITool>(sp => AIFunctionFactory.Create(sp.GetRequiredService<ShellTools>().RunCommand, "run_command"));
        services.AddSingleton<AITool>(sp => AIFunctionFactory.Create(sp.GetRequiredService<MemoryTools>().Remember, "remember"));
        services.AddSingleton<AITool>(sp => AIFunctionFactory.Create(sp.GetRequiredService<MemoryTools>().Recall, "recall"));
        services.AddSingleton<AITool>(sp => AIFunctionFactory.Create(sp.GetRequiredService<MemoryTools>().Forget, "forget"));

        return services;
    }

    /// <summary>Composes the whole stack: harness + tools + the agent core.</summary>
    public static IServiceCollection AddAda(this IServiceCollection services, AdaModelOptions? options = null)
        => services.AddAdaTools().AddAdaCore(options);
}

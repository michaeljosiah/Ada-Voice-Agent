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

        services.TryAddSingleton<FileSystemTools>();
        services.TryAddSingleton<ShellTools>();

        services.AddSingleton<AITool>(sp => AIFunctionFactory.Create(sp.GetRequiredService<FileSystemTools>().ReadFile, "read_file"));
        services.AddSingleton<AITool>(sp => AIFunctionFactory.Create(sp.GetRequiredService<FileSystemTools>().ListDirectory, "list_directory"));
        services.AddSingleton<AITool>(sp => AIFunctionFactory.Create(sp.GetRequiredService<FileSystemTools>().WriteFile, "write_file"));
        services.AddSingleton<AITool>(sp => AIFunctionFactory.Create(sp.GetRequiredService<FileSystemTools>().DeleteFile, "delete_file"));
        services.AddSingleton<AITool>(sp => AIFunctionFactory.Create(sp.GetRequiredService<ShellTools>().RunCommand, "run_command"));

        return services;
    }

    /// <summary>Composes the whole stack: harness + tools + the agent core.</summary>
    public static IServiceCollection AddAda(this IServiceCollection services, AdaModelOptions? options = null)
        => services.AddAdaTools().AddAdaCore(options);
}

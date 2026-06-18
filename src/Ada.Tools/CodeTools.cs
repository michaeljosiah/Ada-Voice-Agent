using System.ComponentModel;
using Ada.Core;

namespace Ada.Tools;

/// <summary>
/// Lets the agent run a short program in a disposable, network-isolated container — the autonomy
/// ladder's container zone (spec §8.8). Because the blast radius is the throwaway sandbox (no network,
/// capped memory, removed on exit), a run needs no per-step approval; the gate is the zone boundary.
/// </summary>
public sealed class CodeTools(ContainerCodeSandbox sandbox)
{
    [Description("Run a short, self-contained program in a disposable, network-isolated sandbox and return its output. " +
                 "Use it for quick computation, data wrangling, or trying out code. Languages: \"csharp\", \"python\", \"javascript\". " +
                 "C# runs as a .NET 10 file-based app — top-level statements and implicit usings, so `Console.WriteLine(\"hi\");` is enough. " +
                 "The sandbox has no network access, so don't fetch URLs or install packages.")]
    public async Task<string> RunCode(
        [Description("Programming language: \"csharp\", \"python\", or \"javascript\".")] string language,
        [Description("The complete program source to execute.")] string code,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(code))
            return "No code was provided.";
        if (!sandbox.Available)
            return "The code sandbox needs Docker, which isn't available right now.";

        var result = await sandbox.RunAsync(new SandboxRequest(language ?? "python", code), ct);
        if (result.Ok)
            return string.IsNullOrEmpty(result.Output) ? "(the program produced no output)" : result.Output;
        return $"The program failed ({result.Reason}).{(string.IsNullOrEmpty(result.Error) ? string.Empty : "\n" + result.Error)}";
    }
}

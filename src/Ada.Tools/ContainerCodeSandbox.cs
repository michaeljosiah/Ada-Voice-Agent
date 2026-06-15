using System.Diagnostics;
using Ada.Core;

namespace Ada.Tools;

/// <summary>
/// Zone 2 of the autonomy ladder (spec §8.8): the AIO-style container sandbox over Docker. Untrusted
/// code runs in a throwaway container with <c>--network none</c> and a memory cap — the blast radius
/// is the disposable container. Available only when Docker is present; otherwise Ada uses Zone 1.
/// </summary>
public sealed class ContainerCodeSandbox : ICodeSandbox
{
    private readonly Lazy<bool> _dockerPresent = new(ProbeDocker);

    public SandboxZone Zone => SandboxZone.LocalContainer;
    public bool Available => _dockerPresent.Value;

    public async Task<SandboxResult> RunAsync(SandboxRequest request, CancellationToken ct = default)
    {
        if (!Available)
            return SandboxResult.Failed("unavailable", "Docker is not available; use the in-process Wasm zone.");

        var (image, exec) = request.Language.ToLowerInvariant() switch
        {
            "python" or "py" => ("python:3-alpine", new[] { "python", "-c", request.Code }),
            "javascript" or "js" or "node" => ("node:alpine", new[] { "node", "-e", request.Code }),
            _ => (null, (string[]?)null),
        };
        if (image is null)
            return SandboxResult.Failed("unsupported", $"No container image configured for '{request.Language}'.");

        var memMb = Math.Max(64, request.MemoryBytes / (1024 * 1024));
        var args = new List<string> { "run", "--rm", "--network", "none", "--memory", $"{memMb}m", "--cpus", "1", image };
        args.AddRange(exec!);
        return await RunDocker(args, ct);
    }

    private static async Task<SandboxResult> RunDocker(IReadOnlyList<string> args, CancellationToken ct)
    {
        var psi = new ProcessStartInfo("docker")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        foreach (var a in args) psi.ArgumentList.Add(a);

        using var proc = Process.Start(psi)!;
        var stdoutTask = proc.StandardOutput.ReadToEndAsync(ct);
        var stderrTask = proc.StandardError.ReadToEndAsync(ct);
        try
        {
            await proc.WaitForExitAsync(ct);
        }
        catch (OperationCanceledException)
        {
            try { proc.Kill(entireProcessTree: true); } catch { /* already gone */ }
            return SandboxResult.Failed("timeout", "Container run was cancelled.");
        }

        var stdout = (await stdoutTask).TrimEnd();
        var stderr = (await stderrTask).TrimEnd();
        return proc.ExitCode == 0
            ? SandboxResult.Ran(stdout)
            : SandboxResult.Failed("error", string.IsNullOrEmpty(stderr) ? $"exit {proc.ExitCode}" : stderr);
    }

    private static bool ProbeDocker()
    {
        try
        {
            var psi = new ProcessStartInfo("docker", "version --format {{.Server.Version}}")
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            using var p = Process.Start(psi);
            if (p is null) return false;
            if (!p.WaitForExit(4000)) { try { p.Kill(); } catch { /* ignore */ } return false; }
            return p.ExitCode == 0;
        }
        catch
        {
            return false;
        }
    }
}

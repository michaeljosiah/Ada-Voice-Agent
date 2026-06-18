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
    private readonly ImageProvisioner _images;

    public ContainerCodeSandbox(ImageProvisioner images) => _images = images;

    // C# runs as a .NET 10 file-based app, which needs a one-time NuGet restore. We warm a shared cache
    // volume once (with network), so every real run executes offline (--network none) against it.
    private const string DotnetImage = "mcr.microsoft.com/dotnet/sdk:10.0";
    private const string DotnetCacheVolume = "ada-dotnet-cache";
    private static volatile bool _csWarmed;
    private static readonly SemaphoreSlim _csWarmLock = new(1, 1);

    public SandboxZone Zone => SandboxZone.LocalContainer;
    public bool Available => _dockerPresent.Value;

    public async Task<SandboxResult> RunAsync(SandboxRequest request, CancellationToken ct = default)
    {
        if (!Available)
            return SandboxResult.Failed("unavailable", "Docker is not available; use the in-process Wasm zone.");

        var lang = request.Language.ToLowerInvariant();
        var image = ImageFor(lang);
        if (image is null)
            return SandboxResult.Failed("unsupported", $"No container image configured for '{request.Language}'.");

        // Lazy-provisioning safety net: if the runtime image isn't on disk yet (prefetch hasn't run, or was
        // turned off), start a background pull and return promptly — far better than `docker run` blocking
        // for minutes on a silent first-time download. With prefetch on, we normally never reach this.
        if (!await _images.ImageExistsAsync(image, ct))
        {
            _images.PullInBackground(image);
            return SandboxResult.Failed("downloading",
                $"Ada is downloading the {Friendly(lang)} runtime in the background (first use only). Ask me to run this again in a moment.");
        }

        if (lang is "csharp" or "cs" or "c#" or "dotnet")
            return await RunCSharpAsync(request, ct);

        var exec = lang is "python" or "py"
            ? new[] { "python", "-c", request.Code }
            : new[] { "node", "-e", request.Code }; // javascript/js/node — the only other case ImageFor allows
        var memMb = Math.Max(64, request.MemoryBytes / (1024 * 1024));
        var args = new List<string> { "run", "--rm", "--network", "none", "--memory", $"{memMb}m", "--cpus", "1", image };
        args.AddRange(exec);
        return await RunDocker(args, ct);
    }

    // The Docker image that backs each supported language (null ⇒ unsupported). Kept in step with
    // ImageProvisioner.Catalog so a prefetch warms exactly what run_code will reach for.
    private static string? ImageFor(string lang) => lang switch
    {
        "csharp" or "cs" or "c#" or "dotnet" => DotnetImage,
        "python" or "py" => "python:3-alpine",
        "javascript" or "js" or "node" => "node:alpine",
        _ => null,
    };

    private static string Friendly(string lang) => lang switch
    {
        "csharp" or "cs" or "c#" or "dotnet" => "C#",
        "python" or "py" => "Python",
        _ => "JavaScript",
    };

    // C# runs as a .NET 10 file-based app inside the SDK image — like the other languages, but a .NET
    // build needs a NuGet restore and more memory/CPU/time than an interpreter. We warm a shared cache
    // once (with network) and then run the snippet network-off against it.
    private static async Task<SandboxResult> RunCSharpAsync(SandboxRequest request, CancellationToken ct)
    {
        var dir = Directory.CreateTempSubdirectory("ada_cs_").FullName;
        try
        {
            await File.WriteAllTextAsync(Path.Combine(dir, "script.cs"), request.Code, ct);
            await EnsureDotnetCacheWarmAsync(ct);
            var memMb = Math.Max(512, request.MemoryBytes / (1024 * 1024)); // a .NET build needs headroom
            return await RunDocker(DotnetRunArgs(dir.Replace('\\', '/'), memMb, networkOff: true), ct);
        }
        finally
        {
            try { Directory.Delete(dir, recursive: true); } catch { /* best effort */ }
        }
    }

    // Seed the shared NuGet cache volume once per process with a throwaway script (network on). User code
    // never runs here — it only populates the cache so real runs can restore with the network cut off.
    private static async Task EnsureDotnetCacheWarmAsync(CancellationToken ct)
    {
        if (_csWarmed) return;
        await _csWarmLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (_csWarmed) return;
            var dir = Directory.CreateTempSubdirectory("ada_csw_").FullName;
            try
            {
                await File.WriteAllTextAsync(Path.Combine(dir, "script.cs"), "System.Console.WriteLine(\"warm\");", ct);
                await RunDocker(DotnetRunArgs(dir.Replace('\\', '/'), 512, networkOff: false), ct);
                _csWarmed = true;
            }
            finally { try { Directory.Delete(dir, recursive: true); } catch { /* best effort */ } }
        }
        finally { _csWarmLock.Release(); }
    }

    private static List<string> DotnetRunArgs(string mount, long memMb, bool networkOff)
    {
        var args = new List<string> { "run", "--rm" };
        if (networkOff) { args.Add("--network"); args.Add("none"); }
        args.AddRange(new[]
        {
            "--memory", $"{memMb}m", "--cpus", "2",
            "-v", $"{DotnetCacheVolume}:/root/.nuget",
            "-v", $"{mount}:/work", "-w", "/work",
            "-e", "DOTNET_NOLOGO=1", "-e", "DOTNET_CLI_TELEMETRY_OPTOUT=1",
            DotnetImage, "dotnet", "run", "script.cs",
        });
        return args;
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

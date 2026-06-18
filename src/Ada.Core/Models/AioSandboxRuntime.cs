using System.Diagnostics;

namespace Ada.Core;

/// <summary>Where Ada's AIO sandbox lives and how to run it.</summary>
public sealed record AioSandboxOptions(
    string Endpoint = "http://127.0.0.1:8080",
    string Image = "ghcr.io/agent-infra/sandbox:latest",
    string ContainerName = "ada-sandbox",
    int Port = 8080,
    string ContainerWorkspace = "/home/gem/workspace",
    string ContainerSkillsDir = "/home/gem/skills");

/// <summary>
/// Manages the AIO Sandbox (<c>agent-infra/sandbox</c>) as Ada's preferred work environment: a Docker
/// container exposing a shell, an isolated filesystem, a real browser and code-exec behind one HTTP API
/// on <c>:8080</c>, plus an MCP endpoint at <c>/mcp</c> that Ada mounts to give the agent those tools.
/// <para>
/// It detects an already-running sandbox and adopts it; otherwise it starts our (possibly stopped)
/// container, or runs a fresh one with the host workspace (<c>%APPDATA%\Ada\workspace</c>) bind-mounted
/// to <c>/home/gem/workspace</c> — so the agent operates on one consistent folder whether it runs in the
/// sandbox or falls back to the host. When Docker or the image is absent it returns <see langword="null"/>
/// and Ada uses host tools instead: the sandbox is <em>preferred, never required</em>. An externally-owned
/// (already-running) sandbox is never stopped.
/// </para>
/// </summary>
public sealed class AioSandboxRuntime : IAsyncDisposable
{
    private readonly bool _managed;        // true ⇒ Ada started this container (and will stop it)
    private readonly string _containerName;

    private AioSandboxRuntime(string endpoint, bool managed, string containerName)
    {
        Endpoint = endpoint;
        _managed = managed;
        _containerName = containerName;
    }

    /// <summary>The sandbox HTTP API base (e.g. http://127.0.0.1:8080).</summary>
    public string Endpoint { get; }

    /// <summary>The MCP endpoint Ada mounts so the agent gets the sandbox's browser/shell/file/code tools.</summary>
    public string McpUrl => $"{Endpoint}/mcp";

    /// <summary>True if Ada launched this container (and will stop it on dispose); false if it adopted one.</summary>
    public bool Managed => _managed;

    /// <summary>Probes the sandbox info endpoint to confirm the API is answering.</summary>
    public static async Task<bool> IsReachableAsync(string endpoint, CancellationToken ct = default)
    {
        try
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(3) };
            return (await http.GetAsync($"{endpoint}/v1/sandbox", ct)).IsSuccessStatusCode;
        }
        catch { return false; }
    }

    /// <summary>True if a Docker daemon is reachable (required to start the sandbox).</summary>
    public static Task<bool> DockerAvailableAsync(CancellationToken ct = default)
        => RunDockerSucceedsAsync(["version", "--format", "{{.Server.Version}}"], ct);

    /// <summary>
    /// Brings the sandbox up. Adopts an already-running one; else starts our stopped container; else
    /// — Docker present and the image local, or <paramref name="allowPull"/> set — runs a fresh one with
    /// the workspace mounted. Returns <see langword="null"/> when it can't come up without a big image
    /// pull and pulling isn't allowed, so app startup never blocks on a multi-GB download (the setup
    /// wizard does that with progress).
    /// </summary>
    public static async Task<AioSandboxRuntime?> StartAsync(AioSandboxOptions options, bool allowPull, IProgress<string>? progress = null, CancellationToken ct = default)
    {
        if (await IsReachableAsync(options.Endpoint, ct))
            return new AioSandboxRuntime(options.Endpoint, managed: false, options.ContainerName); // adopt (the user's, or a prior managed one)

        if (!await DockerAvailableAsync(ct))
            return null; // no Docker → caller falls back to host tools

        if (await ContainerExistsAsync(options.ContainerName, ct))
        {
            progress?.Report("Starting the sandbox…");
            await RunDockerSucceedsAsync(["start", options.ContainerName], ct);
        }
        else
        {
            if (!await ImageExistsAsync(options.Image, ct))
            {
                if (!allowPull) return null; // defer the multi-GB pull to the wizard
                progress?.Report("Pulling the sandbox image (first run — this is large)…");
                if (!await RunDockerSucceedsAsync(["pull", options.Image], ct, TimeSpan.FromMinutes(30))) return null;
            }

            progress?.Report("Starting the sandbox…");
            // Bind-mount the host workspace into the container and tell the sandbox to treat it as the
            // workspace root. Skills are mounted read-only so a skill's bundled scripts can run inside the
            // sandbox without being writable. Forward slashes keep Docker Desktop happy with Windows paths.
            var host = AdaPaths.EnsureWorkspaceDir().Replace('\\', '/');
            var skills = AdaPaths.EnsureSkillsDir().Replace('\\', '/');
            string[] run =
            [
                "run", "-d",
                "--name", options.ContainerName,
                "--security-opt", "seccomp=unconfined",
                "-p", $"{options.Port}:8080",
                "-e", $"WORKSPACE={options.ContainerWorkspace}",
                "-v", $"{host}:{options.ContainerWorkspace}",
                "-v", $"{skills}:{options.ContainerSkillsDir}:ro",
                options.Image,
            ];
            if (!await RunDockerSucceedsAsync(run, ct, TimeSpan.FromMinutes(2))) return null;
        }

        // Give the container's services time to bind the API before we declare it ready.
        for (var i = 0; i < 60 && !await IsReachableAsync(options.Endpoint, ct); i++)
            await Task.Delay(1000, ct);

        return await IsReachableAsync(options.Endpoint, ct)
            ? new AioSandboxRuntime(options.Endpoint, managed: true, options.ContainerName)
            : null;
    }

    private static Task<bool> ContainerExistsAsync(string name, CancellationToken ct)
        => RunDockerSucceedsAsync(["container", "inspect", name], ct);

    private static Task<bool> ImageExistsAsync(string image, CancellationToken ct)
        => RunDockerSucceedsAsync(["image", "inspect", image], ct);

    /// <summary>Runs a docker command and returns true on exit 0. Never throws (missing docker ⇒ false).</summary>
    private static async Task<bool> RunDockerSucceedsAsync(IReadOnlyList<string> args, CancellationToken ct, TimeSpan? timeout = null)
    {
        try
        {
            var psi = new ProcessStartInfo("docker")
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            foreach (var a in args) psi.ArgumentList.Add(a);

            using var p = Process.Start(psi);
            if (p is null) return false;

            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(timeout ?? TimeSpan.FromSeconds(15));
            try { await p.WaitForExitAsync(cts.Token); }
            catch (OperationCanceledException) { try { p.Kill(entireProcessTree: true); } catch { /* gone */ } return false; }

            return p.ExitCode == 0;
        }
        catch { return false; }
    }

    public async ValueTask DisposeAsync()
    {
        // Only stop what we started; an adopted sandbox keeps running. The container is left in place
        // (not removed), so the next launch is a fast `docker start` rather than a fresh `run`.
        if (_managed)
            await RunDockerSucceedsAsync(["stop", _containerName], CancellationToken.None);
    }
}

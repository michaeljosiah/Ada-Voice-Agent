using Ada.Core;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Ada.Server;

/// <summary>
/// Brings Ada's AIO sandbox up (if enabled and available) and mounts its <c>/mcp</c> endpoint so the
/// agent gains the sandbox's shell/file/browser/code tools — falling back silently to host tools when
/// Docker or the image is absent. Like the managed Ollama, the bring-up runs in the background so app
/// startup never blocks; the agent (built lazily on first request) waits briefly on the
/// <see cref="SandboxSession"/> so it always composes against the settled environment.
/// </summary>
internal sealed class SandboxHostedService(SandboxSession session, McpMounter mounter, ILogger<SandboxHostedService> log) : IHostedService
{
    private AioSandboxRuntime? _runtime;

    public Task StartAsync(CancellationToken ct)
    {
        if (!new ConfigStore().Load().SandboxEnabled)
        {
            log.LogInformation("[sandbox] disabled by config — using host tools.");
            return Task.CompletedTask; // session stays ready + inactive
        }

        session.BeginInitialization();
        _ = Task.Run(() => BringUpAsync(ct), ct); // background — never block startup (mirrors managed Ollama)
        return Task.CompletedTask;
    }

    private async Task BringUpAsync(CancellationToken ct)
    {
        try
        {
            // allowPull:false — never block on a multi-GB image pull here; the setup wizard pulls with progress.
            _runtime = await AioSandboxRuntime.StartAsync(new AioSandboxOptions(), allowPull: false, progress: null, ct);
            if (_runtime is null)
            {
                log.LogInformation("[sandbox] not available — using host tools.");
                session.Deactivate();
                return;
            }

            // A loopback, contained zone: its tools run ungated — the gate is the sandbox boundary plus
            // the bind-mounted workspace (already an allowed write root), per the autonomy ladder.
            var mount = new McpMount("sandbox", McpTransport.Http, Url: _runtime.McpUrl, IsEgress: false, GateMutatingTools: false);
            var tools = await mounter.MountAsync(mount, ct);
            session.Activate(_runtime.Endpoint, tools);
            log.LogInformation("[sandbox] active at {Endpoint} — {Count} tools mounted.", _runtime.Endpoint, tools.Count);
        }
        catch (Exception ex)
        {
            log.LogWarning(ex, "[sandbox] bring-up failed — using host tools.");
            session.Deactivate();
        }
    }

    // Leave the container running so the next launch adopts it instantly (warm). An explicit stop lives
    // in Settings; an externally-owned sandbox is never ours to stop anyway.
    public Task StopAsync(CancellationToken ct) => Task.CompletedTask;
}

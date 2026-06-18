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
internal sealed class SandboxHostedService(SandboxSession session, McpMounter mounter, ImageProvisioner images, ILogger<SandboxHostedService> log) : IHostedService
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
        finally
        {
            await MaybePrefetchRuntimesAsync(ct);
        }
    }

    // Once the sandbox itself is set up (its image is on disk), quietly top up any missing run_code runtime
    // images in the background so the first code run is instant. It never starts the big AIO pull — that's
    // only ever the explicit "Set up the sandbox" action — so a launch can't surprise the user with a
    // multi-GB download they didn't ask for.
    private async Task MaybePrefetchRuntimesAsync(CancellationToken ct)
    {
        try
        {
            var cfg = new ConfigStore().Load();
            if (!cfg.SandboxEnabled || !cfg.PrefetchImages) return;
            if (!await images.DockerAvailableAsync(ct)) return;

            var core = ImageProvisioner.Find("aio")!;
            if (!await images.ImageExistsAsync(core.Reference, ct)) return; // wait for explicit setup before any pull

            var present = await images.PrefetchMissingAsync(progress: null, includeCore: false, ct);
            log.LogInformation("[images] runtime prefetch settled — {Present}/{Total} runtime image(s) present.",
                present, ImageProvisioner.Catalog.Count(i => !i.Core));
        }
        catch (Exception ex) { log.LogDebug(ex, "[images] runtime prefetch skipped."); }
    }

    // Leave the container running so the next launch adopts it instantly (warm). An explicit stop lives
    // in Settings; an externally-owned sandbox is never ours to stop anyway.
    public Task StopAsync(CancellationToken ct) => Task.CompletedTask;
}

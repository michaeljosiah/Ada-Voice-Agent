namespace Ada.Core;

/// <summary>Observable state of Ada's managed Ollama, for the launch readiness page.</summary>
public enum OllamaLaunchState { NotConfigured, NotInstalled, Starting, Ready, Failed }

/// <summary>
/// Single-flight supervisor for the managed Ollama. Brings an installed/managed Ollama up on launch
/// (never downloads — the setup wizard owns that, with progress), adopts a user's own running instance
/// without taking ownership, and exposes <see cref="State"/> so the launch page can show "starting → ready"
/// instead of letting the first turn hang on a refused connection. One instance for the app lifetime.
/// </summary>
public static class OllamaLauncher
{
    private static readonly object _gate = new();
    private static Task? _starting;
    private static OllamaRuntime? _runtime;
    private static volatile OllamaLaunchState _state = OllamaLaunchState.NotConfigured;

    /// <summary>Best-known state from the most recent start attempt. Confirm liveness with a reachability probe.</summary>
    public static OllamaLaunchState State => _state;

    /// <summary>
    /// Kick a single background start when Ollama is the configured local runtime. Non-blocking and
    /// idempotent: a start already in flight is left alone, and a finished attempt is only re-kicked if it
    /// didn't reach <see cref="OllamaLaunchState.Ready"/> (so a "retry" from the launch page works).
    /// </summary>
    public static void EnsureStarted(OllamaOptions? options = null)
    {
        lock (_gate)
        {
            if (_starting is { IsCompleted: false }) return; // in flight
            if (_state == OllamaLaunchState.Ready) return;   // already up
            _starting = StartOnceAsync(options ?? new OllamaOptions());
        }
    }

    private static async Task StartOnceAsync(OllamaOptions options)
    {
        if (new ConfigStore().Load().LocalRuntime != "ollama") { _state = OllamaLaunchState.NotConfigured; return; }
        _state = OllamaLaunchState.Starting;
        try
        {
            // allowDownload:false — adopts a running/installed Ollama; returns null only when not installed.
            var runtime = await OllamaRuntime.StartAsync(options, allowDownload: false);
            if (runtime is null) { _state = OllamaLaunchState.NotInstalled; return; }
            lock (_gate) _runtime = runtime;
            _state = await OllamaRuntime.IsReachableAsync(options.Endpoint) ? OllamaLaunchState.Ready : OllamaLaunchState.Failed;
        }
        catch { _state = OllamaLaunchState.Failed; }
    }

    /// <summary>Stop the managed Ollama (a no-op for an adopted external instance). Call on app shutdown.</summary>
    public static async ValueTask ShutdownAsync()
    {
        OllamaRuntime? rt;
        lock (_gate) { rt = _runtime; _runtime = null; }
        if (rt is not null) await rt.DisposeAsync();
    }
}

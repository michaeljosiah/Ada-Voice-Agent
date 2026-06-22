using Ada.Core;
using Ada.Voice;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;

namespace Ada.Server;

/// <summary>Observable state of the one-time model warm-up the launch / voice readiness checks wait on.</summary>
public enum WarmState { Cold, Warming, Ready, Failed }

/// <summary>
/// Single-flight warm-up for the agent's model. A reachable Ollama (or a downloaded ONNX model) is not the
/// same as one that can <em>answer</em>: the first generation pays a cold model-load that the reachability
/// check skips, which is exactly why a turn looked stuck on "thinking" right after the splash. This runs one
/// real (tiny) generation through the same client the agent uses — loading the model into memory and proving
/// it responds — so the splash can hold until the model is genuinely ready and surface a clear failure if it
/// isn't. Shared by <see cref="LaunchStatus"/> (main splash) and the voice widget's readiness gate.
/// </summary>
public static class ModelWarmup
{
    private static readonly object _gate = new();
    private static Task? _task;
    private static volatile WarmState _state = WarmState.Cold;
    private static string _detail = "";

    /// <summary>How long a warm-up generation may take before it's deemed unresponsive (cold loads are slow).</summary>
    public static readonly TimeSpan Timeout = TimeSpan.FromSeconds(40);

    public static WarmState State => _state;
    public static string Detail => _detail;

    /// <summary>
    /// Kick a single background warm-up. Non-blocking and idempotent: a run already in flight is left alone,
    /// and a finished run is only re-kicked if it didn't reach <see cref="WarmState.Ready"/> (so a retry works).
    /// </summary>
    public static void Ensure(IServiceProvider services)
    {
        lock (_gate)
        {
            if (_task is { IsCompleted: false }) return;
            if (_state == WarmState.Ready) return;
            _task = RunAsync(services);
        }
    }

    private static async Task RunAsync(IServiceProvider services)
    {
        _state = WarmState.Warming;
        var registry = services.GetRequiredService<ProviderRegistry>();
        var options = services.GetService<AdaModelOptions>() ?? AdaModelOptions.FromEnvironment();

        IChatClient client;
        try { client = VoicePreflight.ResolveModelClient(registry, options); }
        catch (Exception ex) { _state = WarmState.Failed; _detail = ex.Message; return; }

        try
        {
            if (client is StubChatClient) { _detail = "No model configured — using the offline stand-in."; _state = WarmState.Ready; return; }
            var check = await VoicePreflight.ProbeModelAsync(client, Timeout).ConfigureAwait(false);
            _detail = check.Detail;
            _state = check.Status == PreflightStatus.Fail ? WarmState.Failed : WarmState.Ready; // Ok/Warn(slow) → loaded & responding
        }
        catch (Exception ex) { _state = WarmState.Failed; _detail = ex.Message; }
        finally { (client as IDisposable)?.Dispose(); }
    }
}

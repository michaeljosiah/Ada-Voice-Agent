using Ada.Core;
using Ada.Voice;
using Microsoft.Extensions.DependencyInjection;

namespace Ada.Server;

/// <summary>
/// Aggregates the "is Ada ready to use right now?" checks the launch splash polls: a reachable local/cloud
/// brain — the gating check, whose absence is exactly what makes a turn hang on "thinking" — plus the
/// configured speech models. Reuses <see cref="VoicePreflight"/> for speech-model state and
/// <see cref="OllamaLauncher"/> for an observable, download-free start of a configured-but-not-running Ollama.
/// </summary>
public static class LaunchStatus
{
    public sealed record Item(string Name, string Status, string Detail, string? Action);
    public sealed record Report(bool Ready, bool SetupComplete, string Stage, IReadOnlyList<Item> Checks);

    public static async Task<Report> BuildAsync(IServiceProvider services, CancellationToken ct = default)
    {
        var cfg = new ConfigStore().Load();
        var items = new List<Item>();

        var (brainOk, brainStarting, brain) = await CheckBrainAsync(cfg, services, ct);
        items.Add(brain);

        // Speech models are needed for voice but don't block text, so they never gate "ready".
        foreach (var c in VoicePreflight.CheckSpeechModels(cfg.SttModel, cfg.TtsProvider, cfg.TtsVoice))
            items.Add(new Item(c.Name, MapStatus(c.Status), c.Detail, c.Status == PreflightStatus.Ok ? null : "settings"));

        var stage = !cfg.SetupComplete ? "setup"
            : brainOk ? "ready"
            : brainStarting ? "starting-model"
            : "needs-attention";
        return new Report(cfg.SetupComplete && brainOk, cfg.SetupComplete, stage, items);
    }

    /// <summary>
    /// The gating check. Two stages: (1) the runtime is available (Ollama reachable / ONNX downloaded /
    /// provider configured), then (2) the model actually <em>responds</em> — a warm-up generation, not just
    /// reachability. Stage 2 is the part a silent "stuck on thinking" needed: a reachable Ollama whose model
    /// won't generate now fails loudly here instead of hanging the first turn.
    /// </summary>
    private static async Task<(bool ok, bool starting, Item item)> CheckBrainAsync(AdaConfig cfg, IServiceProvider services, CancellationToken ct)
    {
        const string name = "Local model";

        // --- Stage 1: is the runtime up at all? ---
        if (cfg.LocalRuntime == "ollama")
        {
            var opts = new OllamaOptions();
            if (!await OllamaRuntime.IsReachableAsync(opts.Endpoint, ct))
            {
                OllamaLauncher.EnsureStarted(opts); // observable, never downloads
                return OllamaLauncher.State switch
                {
                    OllamaLaunchState.NotInstalled => (false, false, new(name, "fail", "Ollama isn't installed yet — set up a local model.", "wizard")),
                    OllamaLaunchState.Failed => (false, false, new(name, "fail", "Ollama isn't responding. Open Settings to start it or switch your model.", "settings")),
                    _ => (false, true, new(name, "warn", "Starting your local model…", null)), // Starting / brief NotConfigured window
                };
            }
        }
        else if (cfg.LocalRuntime == "onnx")
        {
            var store = new OnnxModelStore();
            var id = cfg.LocalModelId ?? store.Downloaded().FirstOrDefault();
            if (id is null || !store.IsReady(id))
                return (false, false, new(name, "fail", "No on-device model downloaded yet.", "wizard"));
        }
        else
        {
            var registry = services.GetRequiredService<ProviderRegistry>();
            if (registry.ForRole(ModelRole.Default) is null && registry.ForRole(ModelRole.Escalation) is null)
                return (false, false, new(name, "fail", "No model configured yet.", "wizard"));
        }

        // --- Stage 2: can the model actually generate? Warm it (load + one tiny reply) so the first real
        //     turn is fast, and fail loudly if it never responds. ---
        ModelWarmup.Ensure(services);
        return ModelWarmup.State switch
        {
            WarmState.Ready => (true, false, new(name, "ok", "Ready.", null)),
            WarmState.Failed => (false, false, new(name, "fail",
                $"The model isn't responding ({ModelWarmup.Detail}) — open Settings to re-download it or switch models.", "settings")),
            _ => (false, true, new(name, "warn", "Warming up the model…", null)), // Cold / Warming
        };
    }

    private static string MapStatus(PreflightStatus s) => s switch
    {
        PreflightStatus.Ok => "ok",
        PreflightStatus.Warn => "warn",
        _ => "fail",
    };
}

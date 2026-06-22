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

    /// <summary>The gating check: can Ada reach a brain? Drives the observable Ollama start when needed.</summary>
    private static async Task<(bool ok, bool starting, Item item)> CheckBrainAsync(AdaConfig cfg, IServiceProvider services, CancellationToken ct)
    {
        const string name = "Local model";

        if (cfg.LocalRuntime == "ollama")
        {
            var opts = new OllamaOptions();
            if (await OllamaRuntime.IsReachableAsync(opts.Endpoint, ct))
                return (true, false, new(name, "ok", $"Ollama running · {cfg.OllamaModel ?? opts.DefaultModel}.", null));

            OllamaLauncher.EnsureStarted(opts); // observable, never downloads
            return OllamaLauncher.State switch
            {
                // NotConfigured here is the brief window before the background task sets Starting.
                OllamaLaunchState.Starting or OllamaLaunchState.NotConfigured
                    => (false, true, new(name, "warn", "Starting your local model…", null)),
                OllamaLaunchState.NotInstalled
                    => (false, false, new(name, "fail", "Ollama isn't installed yet — set up a local model.", "wizard")),
                _ => (false, false, new(name, "fail", "Ollama isn't responding. Open Settings to start it or switch your model.", "settings")),
            };
        }

        if (cfg.LocalRuntime == "onnx")
        {
            var store = new OnnxModelStore();
            var id = cfg.LocalModelId ?? store.Downloaded().FirstOrDefault();
            return id is not null && store.IsReady(id)
                ? (true, false, new(name, "ok", $"On-device model ready · {id}.", null))
                : (false, false, new(name, "fail", "No on-device model downloaded yet.", "wizard"));
        }

        // No local runtime configured — fall back to a configured cloud provider, else nothing.
        var registry = services.GetRequiredService<ProviderRegistry>();
        if (registry.ForRole(ModelRole.Default) is not null || registry.ForRole(ModelRole.Escalation) is not null)
            return (true, false, new(name, "ok", "Using a configured provider.", null));
        return (false, false, new(name, "fail", "No model configured yet.", "wizard"));
    }

    private static string MapStatus(PreflightStatus s) => s switch
    {
        PreflightStatus.Ok => "ok",
        PreflightStatus.Warn => "warn",
        _ => "fail",
    };
}

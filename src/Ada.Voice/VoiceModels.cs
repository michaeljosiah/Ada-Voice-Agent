using Voxa.Speech;                 // VoxaModelCache, VoxaModelArtifact, VoxaModelCacheOptions, VoxaPrefetchProgress
using Voxa.Speech.WhisperCpp;      // WhisperCppModelCatalog
using Voxa.Speech.Piper;           // PiperVoiceCatalog, PiperVoice, PiperExecutableCatalog

namespace Ada.Voice;

/// <summary>
/// Reports on, and warms up, the local Voxa speech models Ada.Voice depends on: the WhisperCpp STT
/// model, the Piper TTS voice, and the Piper executable for the current OS. All three resolve through
/// the real <see cref="VoxaModelCache"/> (cache root = <c>VOXA_MODEL_CACHE</c> env or
/// <c>%LOCALAPPDATA%\voxa\models</c>). On our à-la-carte pipeline Voxa's own eager-warmup does not run,
/// so these models download lazily on the first connection unless warmed up here — which the Voice
/// settings panel does explicitly, with progress.
/// </summary>
public static class VoiceModels
{
    private const string WhisperModel = "base.en";
    private const string PiperVoiceName = "en_US-lessac-medium";

    private const int WhisperApproxMb = 148;
    private const int PiperVoiceApproxMb = 63;
    private const int PiperExeApproxMb = 22;

    /// <summary>One row of model-readiness information for display.</summary>
    public sealed record VoiceModelInfo(string Name, string Kind, string Detail, bool Ready, int ApproxMb);

    /// <summary>Pure probe — reports whether each pinned artifact is already cached. Never downloads, never throws.</summary>
    public static IReadOnlyList<VoiceModelInfo> Status()
    {
        VoxaModelCache? cache = TryCreateCache(offline: true);
        return new[]
        {
            BuildStatus($"Whisper {WhisperModel}", "STT", WhisperApproxMb, cache, TryGetWhisperArtifact),
            BuildStatus($"Piper {PiperVoiceName}", "TTS", PiperVoiceApproxMb, cache, TryGetPiperVoiceArtifacts),
            BuildStatus("Piper executable", "TTS", PiperExeApproxMb, cache, TryGetPiperExeArtifact),
        };
    }

    /// <summary>Downloads + SHA-verifies all three artifacts, emitting human-readable progress lines.</summary>
    public static async Task WarmUpAsync(IProgress<string>? progress, CancellationToken ct)
    {
        var cache = new VoxaModelCache(new VoxaModelCacheOptions(VoxaModelCacheOptions.ResolveCacheRoot(), Offline: false));

        var items = new List<(VoxaModelArtifact Artifact, string Label)>();
        if (WhisperCppModelCatalog.TryGet(WhisperModel, out VoxaModelArtifact whisper))
            items.Add((whisper, $"Whisper {WhisperModel}"));
        else
            progress?.Report($"Whisper model '{WhisperModel}' is not in the WhisperCpp catalog; skipping.");

        if (PiperVoiceCatalog.TryGet(PiperVoiceName, out PiperVoice voice))
        {
            items.Add((voice.Onnx, $"Piper voice {PiperVoiceName} (model)"));
            items.Add((voice.Json, $"Piper voice {PiperVoiceName} (config)"));
        }
        else
        {
            progress?.Report($"Piper voice '{PiperVoiceName}' is not in the Piper catalog; skipping.");
        }

        VoxaModelArtifact? piperExe = PiperExecutableCatalog.ForCurrentPlatform();
        if (piperExe is not null)
            items.Add((piperExe, $"Piper executable ({PiperExecutableCatalog.CurrentRid()})"));
        else
            progress?.Report($"No pinned Piper executable for this platform ({PiperExecutableCatalog.CurrentRid()}).");

        if (items.Count == 0) { progress?.Report("No speech artifacts to warm up."); return; }

        var labelsById = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var (artifact, label) in items) labelsById[artifact.Id] = label;

        var prefetchProgress = new Progress<VoxaPrefetchProgress>(p =>
        {
            string label = labelsById.TryGetValue(p.ArtifactId, out string? l) ? l : p.ArtifactId;
            progress?.Report(p.Completed
                ? $"{label} ready ({p.CompletedCount}/{p.TotalCount})."
                : $"Downloading {label}… ({p.CompletedCount + 1}/{p.TotalCount})");
        });

        var artifacts = new List<VoxaModelArtifact>(items.Count);
        foreach (var (artifact, _) in items) artifacts.Add(artifact);

        progress?.Report($"Warming up {artifacts.Count} speech artifact(s) into {cache.Options.CacheRoot}…");
        await cache.PrefetchAsync(artifacts, prefetchProgress, ct).ConfigureAwait(false);
        progress?.Report("All speech models ready.");
    }

    private static VoxaModelCache? TryCreateCache(bool offline)
    {
        try { return new VoxaModelCache(new VoxaModelCacheOptions(VoxaModelCacheOptions.ResolveCacheRoot(), Offline: offline)); }
        catch { return null; }
    }

    private static VoiceModelInfo BuildStatus(
        string name, string kind, int approxMb, VoxaModelCache? cache,
        Func<(IReadOnlyList<VoxaModelArtifact> Artifacts, string Detail)> resolve)
    {
        try
        {
            var (artifacts, detail) = resolve();
            if (artifacts.Count == 0) return new VoiceModelInfo(name, kind, detail, false, approxMb);

            bool ready = cache is not null;
            if (ready)
                foreach (VoxaModelArtifact artifact in artifacts)
                    if (!cache!.IsCached(artifact)) { ready = false; break; }

            return new VoiceModelInfo(name, kind, detail, ready, approxMb);
        }
        catch (Exception ex)
        {
            return new VoiceModelInfo(name, kind, $"probe failed: {ex.Message}", false, approxMb);
        }
    }

    private static (IReadOnlyList<VoxaModelArtifact> Artifacts, string Detail) TryGetWhisperArtifact() =>
        WhisperCppModelCatalog.TryGet(WhisperModel, out VoxaModelArtifact a)
            ? (new[] { a }, a.Id)
            : (Array.Empty<VoxaModelArtifact>(), $"'{WhisperModel}' not in WhisperCpp catalog");

    private static (IReadOnlyList<VoxaModelArtifact> Artifacts, string Detail) TryGetPiperVoiceArtifacts() =>
        PiperVoiceCatalog.TryGet(PiperVoiceName, out PiperVoice voice)
            ? (new[] { voice.Onnx, voice.Json }, $"{voice.Onnx.Id} (+ config)")
            : (Array.Empty<VoxaModelArtifact>(), $"'{PiperVoiceName}' not in Piper voice catalog");

    private static (IReadOnlyList<VoxaModelArtifact> Artifacts, string Detail) TryGetPiperExeArtifact()
    {
        VoxaModelArtifact? a = PiperExecutableCatalog.ForCurrentPlatform();
        return a is not null
            ? (new[] { a }, a.Id)
            : (Array.Empty<VoxaModelArtifact>(), $"no pinned binary for {PiperExecutableCatalog.CurrentRid()}");
    }
}

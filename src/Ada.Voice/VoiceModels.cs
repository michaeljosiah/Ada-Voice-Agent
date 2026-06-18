using Voxa.Speech;                 // VoxaModelCache, VoxaModelArtifact, VoxaModelCacheOptions, VoxaPrefetchProgress
using Voxa.Speech.WhisperCpp;      // WhisperCppModelCatalog
using Voxa.Speech.Piper;           // PiperVoiceCatalog, PiperVoice, PiperExecutableCatalog
using Voxa.Speech.Kokoro;          // KokoroCatalog

namespace Ada.Voice;

/// <summary>
/// Enumerates the local speech catalogs (STT models, Piper + Kokoro TTS voices) and reports / warms up
/// the artifacts a given selection needs, all through the real <see cref="VoxaModelCache"/>. Powers the
/// configurable Settings → Voice panel: pick the STT model, the TTS engine, and the voice.
/// </summary>
public static class VoiceModels
{
    public sealed record VoiceModelInfo(string Name, string Kind, string Detail, bool Ready, int ApproxMb);
    public sealed record SttOption(string Id, string Label);
    public sealed record TtsVoiceOption(string Provider, string Id, string Label, int Rate);

    // ---- Catalog enumeration (for the dropdowns) ----

    public static IReadOnlyList<SttOption> SttModels()
    {
        try { return WhisperCppModelCatalog.KnownModels.Select(id => new SttOption(id, WhisperLabel(id))).ToList(); }
        catch { return new[] { new SttOption("base.en", "Base · English") }; }
    }

    public static IReadOnlyList<TtsVoiceOption> TtsVoices()
    {
        var list = new List<TtsVoiceOption>();
        try
        {
            foreach (var id in PiperVoiceCatalog.KnownVoices)
            {
                var rate = PiperVoiceCatalog.TryGet(id, out PiperVoice v) ? v.SampleRate : 22050;
                list.Add(new TtsVoiceOption("Piper", id, PiperLabel(id, rate), rate));
            }
        }
        catch { /* keep whatever we have */ }
        try
        {
            foreach (var id in KokoroCatalog.KnownVoices)
                list.Add(new TtsVoiceOption("Kokoro", id, KokoroLabel(id), 24000));
        }
        catch { /* Kokoro optional */ }
        return list;
    }

    /// <summary>The audio sample rate the chosen TTS voice emits — the client must play at this rate.</summary>
    public static int OutputRateFor(string ttsProvider, string ttsVoice)
    {
        if (string.Equals(ttsProvider, "Kokoro", StringComparison.OrdinalIgnoreCase)) return 24000;
        return PiperVoiceCatalog.TryGet(ttsVoice, out PiperVoice v) ? v.SampleRate : 22050;
    }

    // ---- Readiness + warmup for a specific selection ----

    public static IReadOnlyList<VoiceModelInfo> Status(string sttModel, string ttsProvider, string ttsVoice)
    {
        VoxaModelCache? cache = TryCreateCache(offline: true);
        bool ReadyAll(IReadOnlyList<VoxaModelArtifact> arts)
        {
            if (cache is null || arts.Count == 0) return false;
            foreach (var a in arts) { try { if (!cache.IsCached(a)) return false; } catch { return false; } }
            return true;
        }

        var list = new List<VoiceModelInfo>();
        var whisper = WhisperCppModelCatalog.TryGet(sttModel, out VoxaModelArtifact w) ? new[] { w } : Array.Empty<VoxaModelArtifact>();
        list.Add(new VoiceModelInfo($"Whisper · {sttModel}", "Speech-to-text", whisper.Length > 0 ? w.Id : sttModel, ReadyAll(whisper), WhisperMb(sttModel)));

        if (string.Equals(ttsProvider, "Kokoro", StringComparison.OrdinalIgnoreCase))
        {
            var voice = KokoroCatalog.TryGetVoice(ttsVoice, out VoxaModelArtifact kv) ? new[] { kv } : Array.Empty<VoxaModelArtifact>();
            list.Add(new VoiceModelInfo($"Kokoro · {ttsVoice}", "Voice", ttsVoice, ReadyAll(voice), 1));
            var eng = new List<VoxaModelArtifact>();
            if (KokoroCatalog.TryGetModel("fp16", out VoxaModelArtifact km)) eng.Add(km);
            var esp = KokoroCatalog.EspeakForCurrentPlatform(); if (esp is not null) eng.Add(esp);
            list.Add(new VoiceModelInfo("Kokoro engine", "Text-to-speech", "model (fp16) + espeak-ng", ReadyAll(eng), 190));
        }
        else
        {
            var voice = PiperVoiceArtifacts(ttsVoice);
            list.Add(new VoiceModelInfo($"Piper · {ttsVoice}", "Voice", ttsVoice, ReadyAll(voice), 63));
            var exe = PiperExecutableCatalog.ForCurrentPlatform();
            list.Add(new VoiceModelInfo("Piper engine", "Text-to-speech", "piper.exe", exe is not null && ReadyAll(new[] { exe }), 22));
        }
        return list;
    }

    public static async Task WarmUpAsync(string sttModel, string ttsProvider, string ttsVoice, IProgress<string>? progress, CancellationToken ct)
    {
        var cache = new VoxaModelCache(new VoxaModelCacheOptions(VoxaModelCacheOptions.ResolveCacheRoot(), Offline: false));
        var items = ArtifactsFor(sttModel, ttsProvider, ttsVoice);
        if (items.Count == 0) { progress?.Report("No speech artifacts to warm up."); return; }

        var labelsById = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var (a, label) in items) labelsById[a.Id] = label;
        var prog = new Progress<VoxaPrefetchProgress>(p =>
        {
            var label = labelsById.TryGetValue(p.ArtifactId, out var l) ? l : p.ArtifactId;
            progress?.Report(p.Completed ? $"{label} ready ({p.CompletedCount}/{p.TotalCount})." : $"Downloading {label}… ({p.CompletedCount + 1}/{p.TotalCount})");
        });

        var artifacts = items.Select(i => i.Artifact).ToList();
        progress?.Report($"Warming up {artifacts.Count} speech artifact(s)…");
        await cache.PrefetchAsync(artifacts, prog, ct).ConfigureAwait(false);
        progress?.Report("All selected voice models ready.");
    }

    // ---- helpers ----

    private static List<(VoxaModelArtifact Artifact, string Label)> ArtifactsFor(string sttModel, string ttsProvider, string ttsVoice)
    {
        var items = new List<(VoxaModelArtifact, string)>();
        if (WhisperCppModelCatalog.TryGet(sttModel, out VoxaModelArtifact w)) items.Add((w, $"Whisper {sttModel}"));

        if (string.Equals(ttsProvider, "Kokoro", StringComparison.OrdinalIgnoreCase))
        {
            if (KokoroCatalog.TryGetModel("fp16", out VoxaModelArtifact km)) items.Add((km, "Kokoro model"));
            if (KokoroCatalog.TryGetVoice(ttsVoice, out VoxaModelArtifact kv)) items.Add((kv, $"Kokoro voice {ttsVoice}"));
            var esp = KokoroCatalog.EspeakForCurrentPlatform(); if (esp is not null) items.Add((esp, "espeak-ng"));
        }
        else
        {
            if (PiperVoiceCatalog.TryGet(ttsVoice, out PiperVoice pv))
            {
                items.Add((pv.Onnx, $"Piper voice {ttsVoice}"));
                items.Add((pv.Json, "Piper voice config"));
            }
            var exe = PiperExecutableCatalog.ForCurrentPlatform(); if (exe is not null) items.Add((exe, "Piper executable"));
        }
        return items;
    }

    private static VoxaModelArtifact[] PiperVoiceArtifacts(string voice)
        => PiperVoiceCatalog.TryGet(voice, out PiperVoice v) ? new[] { v.Onnx, v.Json } : Array.Empty<VoxaModelArtifact>();

    private static VoxaModelCache? TryCreateCache(bool offline)
    {
        try { return new VoxaModelCache(new VoxaModelCacheOptions(VoxaModelCacheOptions.ResolveCacheRoot(), Offline: offline)); }
        catch { return null; }
    }

    private static int WhisperMb(string id)
    {
        var q = id.Contains("-q5_1");
        if (id.StartsWith("tiny")) return q ? 32 : 78;
        if (id.StartsWith("small")) return q ? 190 : 488;
        return q ? 60 : 148; // base
    }

    private static string WhisperLabel(string id)
    {
        var english = id.Contains(".en");
        var quant = id.Contains("-q5_1");
        var size = id.StartsWith("tiny") ? "Tiny" : id.StartsWith("small") ? "Small" : "Base";
        var hint = size == "Tiny" ? "fastest" : size == "Small" ? "most accurate" : "balanced";
        return $"{size} · {(english ? "English" : "multilingual")}{(quant ? " · quantised" : "")} ({hint})";
    }

    private static string PiperLabel(string id, int rate)
    {
        var parts = id.Split('-');
        var name = parts.Length > 1 ? Cap(parts[1]) : id;
        var qual = parts.Length > 2 ? parts[2] : "";
        return $"{LangName(parts[0])} — {name} ({qual}, {rate / 1000.0:0.#} kHz)";
    }

    private static string KokoroLabel(string id)
    {
        var region = id.Length > 0 && id[0] == 'b' ? "English (GB)" : "English (US)";
        var gender = id.Length > 1 && id[1] == 'm' ? "male" : "female";
        var name = Cap(id.Split('_').Last());
        return $"{region} — {name} ({gender}, 24 kHz)";
    }

    private static string LangName(string locale) => locale switch
    {
        "en_US" => "English (US)", "en_GB" => "English (GB)",
        "de_DE" => "German", "es_ES" => "Spanish", "fr_FR" => "French",
        _ => locale,
    };

    private static string Cap(string s) => string.IsNullOrEmpty(s) ? s : char.ToUpper(s[0]) + s[1..];
}

using System.Text.Json;
using System.Text.Json.Serialization;

namespace Ada.Core;

/// <summary>Ada's ready-made trust/autonomy presets (spec §15).</summary>
public enum AdaProfile { Private, Balanced, Power }

/// <summary>What a profile turns on.</summary>
public sealed record ProfileSettings(bool StayLocal, bool AllowCloudEscalation, bool AllowContainerSandbox);

public static class Profiles
{
    public static ProfileSettings For(AdaProfile profile) => profile switch
    {
        AdaProfile.Private => new(StayLocal: true, AllowCloudEscalation: false, AllowContainerSandbox: false),
        AdaProfile.Balanced => new(StayLocal: false, AllowCloudEscalation: true, AllowContainerSandbox: true),
        AdaProfile.Power => new(StayLocal: false, AllowCloudEscalation: true, AllowContainerSandbox: true),
        _ => new(false, true, true),
    };
}

/// <summary>The user's settings — the source for "no terminal, no env vars, no editing JSON".</summary>
public sealed class AdaConfig
{
    public AdaProfile Profile { get; set; } = AdaProfile.Balanced;
    public bool SetupComplete { get; set; }
    public bool Autostart { get; set; }
    public string Hotkey { get; set; } = "Ctrl+Alt+A";

    /// <summary>Which local runtime Ada uses: "ollama" (managed subprocess, default) or "onnx" (in-process). Null until setup.</summary>
    public string? LocalRuntime { get; set; }

    /// <summary>The Ollama model tag (e.g. "gemma3:4b") when <see cref="LocalRuntime"/> is "ollama".</summary>
    public string? OllamaModel { get; set; }

    /// <summary>The downloaded ONNX model Ada uses as her local brain (e.g. "gemma-3-1b"), if any.</summary>
    public string? LocalModelId { get; set; }

    /// <summary>Use the AIO sandbox (Docker) as Ada's work environment when available — the agent's own
    /// browser, shell, code-exec and filesystem. Default on; falls back to host tools when it can't start.</summary>
    public bool SandboxEnabled { get; set; } = true;

    /// <summary>Once the sandbox is set up, quietly top up any missing <c>run_code</c> runtime images in the
    /// background on launch so capabilities are warm before the agent needs them. Never starts the big AIO
    /// download itself — that's only ever the explicit "Set up the sandbox" action.</summary>
    public bool PrefetchImages { get; set; } = true;

    // ---- Voice pipeline (Settings → Voice). All local; verified against the Voxa catalogs. ----

    /// <summary>WhisperCpp STT model, e.g. "base.en" / "tiny.en" / "small.en" (+ "-q5_1" quantised).</summary>
    public string SttModel { get; set; } = "base.en";

    /// <summary>STT language ("en", or "auto" to detect).</summary>
    public string SttLanguage { get; set; } = "en";

    /// <summary>Local TTS engine: "Piper" or "Kokoro".</summary>
    public string TtsProvider { get; set; } = "Piper";

    /// <summary>The TTS voice id for <see cref="TtsProvider"/> (e.g. "en_US-lessac-medium" / "af_heart").</summary>
    public string TtsVoice { get; set; } = "en_US-lessac-medium";
}

public sealed class ConfigStore
{
    private static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() },
    };

    private readonly string _path;

    public ConfigStore(string? path = null) => _path = path ?? Path.Combine(AdaPaths.DataDir, "config.json");

    public AdaConfig Load()
    {
        if (!File.Exists(_path)) return new AdaConfig();
        try { return JsonSerializer.Deserialize<AdaConfig>(File.ReadAllText(_path), Json) ?? new AdaConfig(); }
        catch (JsonException) { return new AdaConfig(); }
    }

    public void Save(AdaConfig config)
    {
        AdaPaths.EnsureDataDir();
        File.WriteAllText(_path, JsonSerializer.Serialize(config, Json));
    }
}

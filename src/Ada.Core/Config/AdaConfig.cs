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

    /// <summary>The downloaded ONNX model Ada uses as her local brain (e.g. "gemma-3-1b"), if any.</summary>
    public string? LocalModelId { get; set; }
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

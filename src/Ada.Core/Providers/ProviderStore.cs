using System.Text.Json;
using System.Text.Json.Serialization;

namespace Ada.Core;

/// <summary>
/// Persists the user's configured providers as plain JSON at <c>%APPDATA%\Ada\providers.json</c>.
/// No secrets are written here — only provider shape; the keys live in the <see cref="ICredentialVault"/>.
/// </summary>
public sealed class ProviderStore
{
    private static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() },
    };

    private readonly string _path;

    public ProviderStore(string? path = null) => _path = path ?? Path.Combine(AdaPaths.DataDir, "providers.json");

    public List<ProviderConfig> Load()
    {
        if (!File.Exists(_path)) return [];
        try { return JsonSerializer.Deserialize<List<ProviderConfig>>(File.ReadAllText(_path), Json) ?? []; }
        catch (JsonException) { return []; }
    }

    public void Save(IEnumerable<ProviderConfig> providers)
    {
        AdaPaths.EnsureDataDir();
        File.WriteAllText(_path, JsonSerializer.Serialize(providers, Json));
    }

    public void Upsert(ProviderConfig provider)
    {
        var list = Load();
        list.RemoveAll(p => string.Equals(p.Id, provider.Id, StringComparison.OrdinalIgnoreCase));
        list.Add(provider);
        Save(list);
    }

    public void Remove(string id)
    {
        var list = Load();
        list.RemoveAll(p => string.Equals(p.Id, id, StringComparison.OrdinalIgnoreCase));
        Save(list);
    }
}

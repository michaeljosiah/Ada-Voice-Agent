using System.Text.Json;
using System.Text.Json.Serialization;

namespace Ada.Core;

/// <summary>
/// Persists the user's connected mailboxes. The non-secret account shape is plain JSON at
/// <c>%APPDATA%\Ada\mail-accounts.json</c>; the OAuth refresh token and any client secret go to the
/// <see cref="ICredentialVault"/> (DPAPI), keyed by account id — mirroring how provider keys are kept
/// out of <c>providers.json</c>. Removing an account also wipes its secrets.
/// </summary>
public sealed class MailAccountStore
{
    private static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() },
    };

    private readonly string _path;
    private readonly ICredentialVault _vault;

    public MailAccountStore(ICredentialVault vault, string? path = null)
    {
        _vault = vault;
        _path = path ?? Path.Combine(AdaPaths.DataDir, "mail-accounts.json");
    }

    public List<MailAccount> Load()
    {
        if (!File.Exists(_path)) return [];
        try { return JsonSerializer.Deserialize<List<MailAccount>>(File.ReadAllText(_path), Json) ?? []; }
        catch (JsonException) { return []; }
    }

    public void Save(IEnumerable<MailAccount> accounts)
    {
        AdaPaths.EnsureDataDir();
        File.WriteAllText(_path, JsonSerializer.Serialize(accounts, Json));
    }

    public void Upsert(MailAccount account)
    {
        var list = Load();
        list.RemoveAll(a => string.Equals(a.Id, account.Id, StringComparison.OrdinalIgnoreCase));
        list.Add(account);
        Save(list);
    }

    public void Remove(string id)
    {
        var list = Load();
        list.RemoveAll(a => string.Equals(a.Id, id, StringComparison.OrdinalIgnoreCase));
        Save(list);
        _vault.Delete(RefreshKey(id));
        _vault.Delete(SecretKey(id));
        _vault.Delete(AuthRecordKey(id));
    }

    // ---- secrets (vault) ----
    public void SetRefreshToken(string id, string token) => _vault.Set(RefreshKey(id), token);
    public string? GetRefreshToken(string id) => _vault.Get(RefreshKey(id));
    public void SetClientSecret(string id, string secret) => _vault.Set(SecretKey(id), secret);
    public string? GetClientSecret(string id) => _vault.Get(SecretKey(id));

    /// <summary>The serialized Azure.Identity AuthenticationRecord — identifies the signed-in user so a
    /// read can refresh the access token silently. The actual tokens live in Azure.Identity's own
    /// OS-encrypted cache; this is just the pointer, kept in the vault alongside the other per-account secrets.</summary>
    public void SetAuthRecord(string id, string record) => _vault.Set(AuthRecordKey(id), record);
    public string? GetAuthRecord(string id) => _vault.Get(AuthRecordKey(id));

    private static string RefreshKey(string id) => $"mail.refresh.{id}";
    private static string SecretKey(string id) => $"mail.clientsecret.{id}";
    private static string AuthRecordKey(string id) => $"mail.authrecord.{id}";
}

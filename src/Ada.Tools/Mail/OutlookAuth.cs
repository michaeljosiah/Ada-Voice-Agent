using Azure.Core;
using Azure.Identity;
using Ada.Core;

namespace Ada.Tools;

/// <summary>
/// Microsoft/Outlook sign-in, owned by Ada. Uses Azure.Identity's device-code flow against Ada's own
/// Azure "public client" app (the client id is config, not a secret) — so the user signs in with a code,
/// no app registration of their own. Access/refresh tokens are cached by Azure.Identity in the OS-encrypted
/// store; the per-account <see cref="AuthenticationRecord"/> (which lets reads refresh silently) is kept in
/// Ada's vault. Reads never prompt — an expired account simply yields nothing until it's reconnected.
/// </summary>
public sealed class OutlookAuth(MailAccountStore store)
{
    // Graph delegated scopes for read-only triage. offline_access (refresh tokens) is handled by the credential.
    public static readonly string[] Scopes =
        ["https://graph.microsoft.com/Mail.Read", "https://graph.microsoft.com/User.Read"];

    private const string Tenant = "common";          // work + personal Microsoft accounts
    private const string CacheName = "ada-mail";     // Azure.Identity's encrypted token cache

    private static string? ConfiguredClientId => new ConfigStore().Load().MailClientId;

    /// <summary>Interactive device-code sign-in for a new Outlook account. Surfaces the code+URL through
    /// <paramref name="showCode"/>, then persists the account and its auth record. Returns the account.</summary>
    public async Task<MailAccount> ConnectOutlookAsync(Func<string, Task> showCode, CancellationToken ct = default)
    {
        var clientId = ConfiguredClientId
            ?? throw new InvalidOperationException("No Microsoft app client id is set. Configure it first (Settings → Email, or `ada mail clientid <id>`).");

        var credential = new DeviceCodeCredential(new DeviceCodeCredentialOptions
        {
            ClientId = clientId,
            TenantId = Tenant,
            TokenCachePersistenceOptions = new TokenCachePersistenceOptions { Name = CacheName },
            DeviceCodeCallback = (info, _) => showCode(info.Message),
        });

        var record = await credential.AuthenticateAsync(new TokenRequestContext(Scopes), ct).ConfigureAwait(false);
        var address = string.IsNullOrWhiteSpace(record.Username) ? "unknown" : record.Username;
        var id = $"outlook:{address.ToLowerInvariant()}";

        store.SetAuthRecord(id, await SerializeAsync(record, ct).ConfigureAwait(false));
        var account = new MailAccount(id, MailProviderKind.Outlook, address, address, clientId, DateTimeOffset.UtcNow);
        store.Upsert(account);
        return account;
    }

    /// <summary>A silent (never-prompting) credential for an already-connected account, for reads. Null when
    /// the account has no stored auth record or no client id.</summary>
    public async Task<TokenCredential?> SilentCredentialForAsync(MailAccount account, CancellationToken ct = default)
    {
        var clientId = account.ClientId ?? ConfiguredClientId;
        var recordB64 = store.GetAuthRecord(account.Id);
        if (clientId is null || string.IsNullOrEmpty(recordB64)) return null;

        AuthenticationRecord record;
        try { record = await DeserializeAsync(recordB64, ct).ConfigureAwait(false); }
        catch { return null; }

        return new DeviceCodeCredential(new DeviceCodeCredentialOptions
        {
            ClientId = clientId,
            TenantId = Tenant,
            TokenCachePersistenceOptions = new TokenCachePersistenceOptions { Name = CacheName },
            AuthenticationRecord = record,
            DisableAutomaticAuthentication = true, // never pop a device-code prompt during a read — fail instead
        });
    }

    private static async Task<string> SerializeAsync(AuthenticationRecord record, CancellationToken ct)
    {
        using var ms = new MemoryStream();
        await record.SerializeAsync(ms, ct).ConfigureAwait(false);
        return Convert.ToBase64String(ms.ToArray());
    }

    private static async Task<AuthenticationRecord> DeserializeAsync(string base64, CancellationToken ct)
    {
        using var ms = new MemoryStream(Convert.FromBase64String(base64));
        return await AuthenticationRecord.DeserializeAsync(ms, ct).ConfigureAwait(false);
    }
}

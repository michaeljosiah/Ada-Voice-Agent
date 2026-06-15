using Microsoft.Extensions.AI;

namespace Ada.Core;

/// <summary>
/// Reads the configured providers and builds <see cref="IChatClient"/>s per role (spec §6.7). The
/// hybrid router uses the Default-role provider as the local brain and the Escalation-role provider
/// as the cloud brain.
/// </summary>
public sealed class ProviderRegistry(ProviderStore store, ICredentialVault vault)
{
    public IReadOnlyList<ProviderConfig> Configured => store.Load();

    public ProviderConfig? ForRole(ModelRole role) =>
        Configured.FirstOrDefault(p => p.Role == role);

    public IChatClient? CreateForRole(ModelRole role)
    {
        var provider = ForRole(role);
        return provider is null ? null : ProviderClientFactory.Create(provider, vault);
    }
}

namespace Ada.Core;

/// <summary>
/// The one place the agent's email tools go through: it owns the connected accounts and the per-provider
/// connectors, and fans a query out across every account, merging the results newest-first. A single
/// account erroring (expired token, network) never sinks the rest. Phase 1 is read-only.
/// </summary>
public sealed class MailService
{
    private readonly MailAccountStore _store;
    private readonly Dictionary<MailProviderKind, IMailProvider> _providers;

    public MailService(MailAccountStore store, IEnumerable<IMailProvider> providers)
    {
        _store = store;
        // Last registration wins per kind, so a real provider can override a stub.
        _providers = providers.GroupBy(p => p.Kind).ToDictionary(g => g.Key, g => g.Last());
    }

    public IReadOnlyList<MailAccount> Accounts() => _store.Load();

    /// <summary>True once at least one connected account has a working connector behind it.</summary>
    public bool Ready() => _store.Load().Any(a => _providers.ContainsKey(a.Provider));

    /// <summary>Recent messages across all matching accounts, newest first, capped at <c>query.Max</c>.</summary>
    public async Task<IReadOnlyList<MailSummary>> ListAcrossAsync(MailQuery query, string? accountFilter = null, CancellationToken ct = default)
    {
        var results = new List<MailSummary>();
        foreach (var account in _store.Load().Where(a => Matches(a, accountFilter)))
        {
            if (!_providers.TryGetValue(account.Provider, out var provider)) continue;
            try { results.AddRange(await provider.ListAsync(account, query, ct)); }
            catch (OperationCanceledException) { throw; }
            catch { /* one mailbox failing shouldn't sink triage across the others */ }
        }
        return results.OrderByDescending(m => m.ReceivedAt).Take(Math.Max(1, query.Max)).ToList();
    }

    /// <summary>One message's header + body, or null when the account/id can't be resolved.</summary>
    public async Task<MailBody?> GetAsync(string accountId, string messageId, CancellationToken ct = default)
    {
        var account = _store.Load().FirstOrDefault(a => string.Equals(a.Id, accountId, StringComparison.OrdinalIgnoreCase));
        if (account is null || !_providers.TryGetValue(account.Provider, out var provider)) return null;
        return await provider.GetAsync(account, messageId, ct);
    }

    private static bool Matches(MailAccount a, string? filter) =>
        string.IsNullOrWhiteSpace(filter) ||
        a.Address.Contains(filter, StringComparison.OrdinalIgnoreCase) ||
        a.DisplayName.Contains(filter, StringComparison.OrdinalIgnoreCase) ||
        a.Id.Contains(filter, StringComparison.OrdinalIgnoreCase);
}

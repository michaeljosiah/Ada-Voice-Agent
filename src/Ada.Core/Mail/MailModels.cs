namespace Ada.Core;

/// <summary>The mail backends Ada can connect to.</summary>
public enum MailProviderKind { Gmail, Outlook }

/// <summary>
/// A connected mailbox. The non-secret shape lives in <c>mail-accounts.json</c> (via
/// <see cref="MailAccountStore"/>); the OAuth refresh token and any client secret live in the
/// credential vault, keyed by <see cref="Id"/> — never on disk in the clear. <see cref="ClientId"/> is
/// the user's own OAuth client id (per-user setup), which is a public identifier, not a secret.
/// </summary>
public sealed record MailAccount(
    string Id,
    MailProviderKind Provider,
    string Address,
    string DisplayName,
    string? ClientId = null,
    DateTimeOffset? ConnectedAt = null);

/// <summary>A normalized message header for triage — the same shape whether it came from Gmail or Graph.</summary>
public sealed record MailSummary(
    string AccountId,
    string Address,
    MailProviderKind Provider,
    string Id,
    string ThreadId,
    string From,
    string Subject,
    string Snippet,
    DateTimeOffset ReceivedAt,
    bool IsUnread,
    bool IsImportant,
    IReadOnlyList<string> Labels);

/// <summary>A message's normalized header plus its plain-text body.</summary>
public sealed record MailBody(MailSummary Summary, string BodyText);

/// <summary>What to list. Read-only triage defaults to recent unread.</summary>
public sealed record MailQuery(bool UnreadOnly = true, int Max = 25, string? Search = null);

/// <summary>
/// A read-only connector to one mail backend. Implementations (Gmail, Graph) handle their own auth via
/// the account's vaulted tokens and return the normalized shapes above. Phase 1 is read-only — there are
/// deliberately no archive/move/send members here yet.
/// </summary>
public interface IMailProvider
{
    MailProviderKind Kind { get; }

    /// <summary>Recent messages for one account, newest first.</summary>
    Task<IReadOnlyList<MailSummary>> ListAsync(MailAccount account, MailQuery query, CancellationToken ct = default);

    /// <summary>One message's header + body text, or null if it can't be found.</summary>
    Task<MailBody?> GetAsync(MailAccount account, string messageId, CancellationToken ct = default);
}

using System.ComponentModel;
using Ada.Core;

namespace Ada.Tools;

/// <summary>
/// The agent's read-only window onto the user's connected mailboxes (Gmail + Outlook), for triage. All
/// three tools are read-only — they never modify a mailbox — so they run ungated: the consent boundary
/// was connecting the account in Settings. They operate across every connected account at once.
/// </summary>
public sealed class EmailTools(MailService mail)
{
    [Description("List the email accounts connected to Ada (provider + address + id). Read-only.")]
    public string EmailAccounts()
    {
        var accounts = mail.Accounts();
        if (accounts.Count == 0)
            return "No email accounts are connected yet. The user can add Gmail or Outlook accounts in Settings → Email.";
        return string.Join("\n", accounts.Select(a => $"- {a.DisplayName} <{a.Address}> [{a.Provider}] id={a.Id}"));
    }

    [Description("List recent emails across the connected accounts (newest first) for triage. Read-only — it never " +
                 "archives, deletes, replies or moves. Each line ends with an id; pass that id plus the account to read_email.")]
    public async Task<string> ListEmail(
        [Description("Optional: limit to one account by address, name, or id; omit for all accounts.")] string? account = null,
        [Description("Only unread messages (default true).")] bool unreadOnly = true,
        [Description("Maximum messages to return (1–100, default 25).")] int max = 25,
        [Description("Optional search text passed to the provider (e.g. a sender or keyword).")] string? search = null,
        CancellationToken ct = default)
    {
        if (mail.Accounts().Count == 0)
            return "No email accounts are connected. Add one in Settings → Email first.";
        if (!mail.Ready())
            return "Email accounts are configured but no connector is available yet (the provider integration isn't set up in this build).";

        var query = new MailQuery(unreadOnly, Math.Clamp(max, 1, 100), string.IsNullOrWhiteSpace(search) ? null : search);
        var messages = await mail.ListAcrossAsync(query, account, ct);
        if (messages.Count == 0)
            return unreadOnly ? "No unread emails in the connected accounts." : "No matching emails.";
        return string.Join("\n", messages.Select(Format));
    }

    [Description("Read one email's sender, subject and body text. Read-only. Use the account id and message id from list_email.")]
    public async Task<string> ReadEmail(
        [Description("The account id (from list_email or email_accounts).")] string account,
        [Description("The message id (from list_email).")] string id,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(account) || string.IsNullOrWhiteSpace(id))
            return "Provide both the account id and the message id (both come from list_email).";

        var body = await mail.GetAsync(account, id, ct);
        if (body is null)
            return "Couldn't find that email — re-check the account id and message id from list_email.";

        var s = body.Summary;
        var header = $"From: {s.From}\nSubject: {s.Subject}\nAccount: {s.Address}\nReceived: {s.ReceivedAt:g}\n";
        var text = body.BodyText.Length > 6000 ? body.BodyText[..6000] + "\n…(truncated)" : body.BodyText;
        return header + "\n" + text;
    }

    // A compact, scannable triage line: unread/important flags, account, sender, subject, time, and the id.
    private static string Format(MailSummary m)
    {
        var flags = (m.IsUnread ? "•" : " ") + (m.IsImportant ? "★" : " ");
        return $"{flags} [{m.Address}] {m.From} — {m.Subject}  ({m.ReceivedAt:MMM d HH:mm})  id={m.Id}";
    }
}

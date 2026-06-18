using Ada.Core;

namespace Ada.Tools;

/// <summary>
/// A canned <see cref="IMailProvider"/> with a handful of realistic messages, used to exercise the mail
/// plumbing (store → service → tools → skill) end-to-end without a live mailbox. It is NOT registered in
/// production DI — the real Gmail/Graph connectors are; this exists for the self-test and demos.
/// </summary>
public sealed class SampleMailProvider : IMailProvider
{
    public MailProviderKind Kind { get; }

    public SampleMailProvider(MailProviderKind kind = MailProviderKind.Gmail) => Kind = kind;

    public Task<IReadOnlyList<MailSummary>> ListAsync(MailAccount account, MailQuery query, CancellationToken ct = default)
    {
        var all = Seed(account);
        IEnumerable<MailSummary> q = all;
        if (query.UnreadOnly) q = q.Where(m => m.IsUnread);
        if (!string.IsNullOrWhiteSpace(query.Search))
            q = q.Where(m => (m.From + " " + m.Subject + " " + m.Snippet).Contains(query.Search, StringComparison.OrdinalIgnoreCase));
        return Task.FromResult<IReadOnlyList<MailSummary>>(
            q.OrderByDescending(m => m.ReceivedAt).Take(query.Max).ToList());
    }

    public Task<MailBody?> GetAsync(MailAccount account, string messageId, CancellationToken ct = default)
    {
        var m = Seed(account).FirstOrDefault(x => x.Id == messageId);
        return Task.FromResult(m is null
            ? null
            : new MailBody(m, $"{m.Snippet}\n\n(Full sample body for \"{m.Subject}\".)"));
    }

    private List<MailSummary> Seed(MailAccount a)
    {
        var now = new DateTimeOffset(2026, 6, 18, 12, 0, 0, TimeSpan.Zero); // fixed so tests are deterministic
        MailSummary M(string id, string from, string subject, string snippet, double agoH, bool unread, bool important, params string[] labels)
            => new(a.Id, a.Address, a.Provider, id, "t-" + id, from, subject, snippet, now.AddHours(-agoH), unread, important, labels);

        return
        [
            M("m1", "Dana Whitfield <dana@acme.com>", "Re: contract signature needed today", "Can you sign before 3pm so we can file?", 1.0, true, true, "INBOX", "IMPORTANT"),
            M("m2", "GitHub <notifications@github.com>", "[ada] CI failed on main", "The build failed on the latest push.", 2.5, true, false, "INBOX"),
            M("m3", "Stripe <receipts@stripe.com>", "Your receipt from Stripe", "Payment of $20.00 received.", 5.0, true, false, "INBOX"),
            M("m4", "Weekly Dev Digest <news@digest.io>", "10 links you missed this week", "Curated reading for engineers.", 20.0, true, false, "INBOX", "CATEGORY_PROMOTIONS"),
            M("m5", "Mum <mum@example.com>", "dinner sunday?", "Are you free this weekend?", 30.0, false, true, "INBOX"),
        ];
    }
}

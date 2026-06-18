using System.Net;
using System.Text.RegularExpressions;
using Ada.Core;
using Microsoft.Graph;
using Microsoft.Graph.Models;

namespace Ada.Tools;

/// <summary>
/// Read-only Outlook connector over Microsoft Graph (delegated <c>Mail.Read</c>) — fully Ada-owned. It uses
/// the account's silently-refreshing Microsoft credential (via <see cref="OutlookAuth"/>) and maps Graph
/// messages onto Ada's normalized <see cref="MailSummary"/>/<see cref="MailBody"/>. No write surface exists.
/// </summary>
public sealed partial class OutlookProvider(OutlookAuth auth) : IMailProvider
{
    public MailProviderKind Kind => MailProviderKind.Outlook;

    public async Task<IReadOnlyList<MailSummary>> ListAsync(MailAccount account, MailQuery query, CancellationToken ct = default)
    {
        var graph = await GraphFor(account, ct).ConfigureAwait(false);
        if (graph is null) return [];

        var searching = !string.IsNullOrWhiteSpace(query.Search);
        var page = await graph.Me.Messages.GetAsync(rc =>
        {
            rc.QueryParameters.Top = Math.Clamp(query.Max, 1, 100);
            rc.QueryParameters.Select = ["id", "conversationId", "from", "subject", "bodyPreview", "receivedDateTime", "isRead", "importance", "categories"];
            // $search can't be combined with $filter/$orderby on messages, so it's one or the other.
            if (searching)
                rc.QueryParameters.Search = $"\"{query.Search}\"";
            else
            {
                rc.QueryParameters.Orderby = ["receivedDateTime desc"];
                if (query.UnreadOnly) rc.QueryParameters.Filter = "isRead eq false";
            }
        }, ct).ConfigureAwait(false);

        var results = new List<MailSummary>();
        foreach (var m in page?.Value ?? [])
            results.Add(ToSummary(account, m));
        return results;
    }

    public async Task<MailBody?> GetAsync(MailAccount account, string messageId, CancellationToken ct = default)
    {
        var graph = await GraphFor(account, ct).ConfigureAwait(false);
        if (graph is null) return null;

        var m = await graph.Me.Messages[messageId].GetAsync(rc =>
        {
            rc.QueryParameters.Select = ["id", "conversationId", "from", "subject", "bodyPreview", "receivedDateTime", "isRead", "importance", "categories", "body"];
        }, ct).ConfigureAwait(false);
        if (m is null) return null;

        var text = m.Body?.ContentType == BodyType.Html ? HtmlToText(m.Body?.Content) : (m.Body?.Content ?? m.BodyPreview ?? string.Empty);
        return new MailBody(ToSummary(account, m), text);
    }

    private async Task<GraphServiceClient?> GraphFor(MailAccount account, CancellationToken ct)
    {
        var credential = await auth.SilentCredentialForAsync(account, ct).ConfigureAwait(false);
        return credential is null ? null : new GraphServiceClient(credential, OutlookAuth.Scopes);
    }

    private static MailSummary ToSummary(MailAccount a, Message m) => new(
        a.Id, a.Address, a.Provider,
        m.Id ?? string.Empty,
        m.ConversationId ?? string.Empty,
        m.From?.EmailAddress?.Name ?? m.From?.EmailAddress?.Address ?? "(unknown sender)",
        m.Subject ?? "(no subject)",
        m.BodyPreview ?? string.Empty,
        m.ReceivedDateTime ?? DateTimeOffset.MinValue,
        m.IsRead == false,
        m.Importance == Importance.High,
        m.Categories?.ToList() ?? []);

    // Crude HTML → text: drop script/style, strip tags, decode entities, collapse whitespace. Enough for
    // triage and summarisation; we're not trying to render the email.
    private static string HtmlToText(string? html)
    {
        if (string.IsNullOrEmpty(html)) return string.Empty;
        var t = ScriptStyle().Replace(html, " ");
        t = Tags().Replace(t, " ");
        t = WebUtility.HtmlDecode(t);
        return Whitespace().Replace(t, " ").Trim();
    }

    [GeneratedRegex("<(script|style)[^>]*>.*?</\\1>", RegexOptions.Singleline | RegexOptions.IgnoreCase)]
    private static partial Regex ScriptStyle();
    [GeneratedRegex("<[^>]+>")]
    private static partial Regex Tags();
    [GeneratedRegex("\\s+")]
    private static partial Regex Whitespace();
}

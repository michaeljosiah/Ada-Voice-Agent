using System.ComponentModel;
using Ada.Core;

namespace Ada.Tools;

/// <summary>Ada's reach to the web. Fetching leaves the machine, so it is treated as egress and logged.</summary>
public sealed class WebTools(IAuditLog audit) : IDisposable
{
    private readonly HttpClient _http = new() { Timeout = TimeSpan.FromSeconds(20) };

    [Description("Fetch the text content at an http(s) URL. This leaves the machine (egress) and is logged.")]
    public async Task<string> WebFetch([Description("The absolute http(s) URL to fetch.")] string url)
    {
        await audit.RecordAsync(new AuditEntry("web_fetch", url, RiskTier.Medium, "egress"));
        try
        {
            var text = await _http.GetStringAsync(url);
            return text.Length > 8000 ? text[..8000] + "\n…[truncated]" : text;
        }
        catch (Exception ex)
        {
            return $"Could not fetch '{url}': {ex.Message}";
        }
    }

    [Description("Search the web for a query. Requires a configured search provider.")]
    public Task<string> WebSearch([Description("The search query.")] string query)
        => Task.FromResult("web_search needs a configured search provider. Give Ada a URL and use web_fetch, or configure a search provider in settings.");

    public void Dispose() => _http.Dispose();
}

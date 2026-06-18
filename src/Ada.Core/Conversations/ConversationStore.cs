using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace Ada.Core;

/// <summary>One message in a conversation thread.</summary>
public sealed class ConversationMessage
{
    public string Role { get; set; } = "user";   // "user" | "assistant" | "system"
    public string Text { get; set; } = "";
    public string? Route { get; set; }            // where an assistant turn was served (for the route badge)
    public string Ts { get; set; } = "";          // ISO-8601 UTC
}

/// <summary>A conversation thread — the full, never-lossy transcript Ada persists per chat.</summary>
public sealed class Conversation
{
    public string Id { get; set; } = "";
    public string Title { get; set; } = "New chat";
    public string CreatedAt { get; set; } = "";
    public string UpdatedAt { get; set; } = "";
    public List<ConversationMessage> Messages { get; set; } = [];
}

/// <summary>A lightweight row for the history list.</summary>
public sealed record ConversationSummary(string Id, string Title, string UpdatedAt, int MessageCount);

/// <summary>Persists conversation threads (spec: durable history, distinct from semantic memory).</summary>
public interface IConversationStore
{
    Conversation Create(string? title);
    Conversation? Load(string id);
    void Save(Conversation conversation);
    IReadOnlyList<ConversationSummary> List();
    bool Delete(string id);
}

/// <summary>
/// Stores each conversation thread as a JSON file under <c>%APPDATA%\Ada\conversations\&lt;id&gt;.json</c> —
/// plain, inspectable, one file per thread. The <em>full</em> transcript is kept; compaction only ever
/// shapes what the model sees, never what's stored, so a thread reopens exactly as it was.
/// </summary>
public sealed partial class FileConversationStore : IConversationStore
{
    private static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    private readonly string _dir;

    public FileConversationStore(string? dir = null)
        => _dir = dir ?? Path.Combine(AdaPaths.DataDir, "conversations");

    public Conversation Create(string? title)
    {
        var now = Now();
        var convo = new Conversation
        {
            Id = Guid.NewGuid().ToString("n"),
            Title = Clean(title) ?? "New chat",
            CreatedAt = now,
            UpdatedAt = now,
        };
        Save(convo);
        return convo;
    }

    public Conversation? Load(string id)
    {
        var path = PathFor(id);
        if (path is null || !File.Exists(path)) return null;
        try { return JsonSerializer.Deserialize<Conversation>(File.ReadAllText(path), Json); }
        catch (JsonException) { return null; }
    }

    public void Save(Conversation conversation)
    {
        var path = PathFor(conversation.Id);
        if (path is null) return;
        Directory.CreateDirectory(_dir);
        conversation.UpdatedAt = Now();
        File.WriteAllText(path, JsonSerializer.Serialize(conversation, Json));
    }

    public IReadOnlyList<ConversationSummary> List()
    {
        if (!Directory.Exists(_dir)) return [];
        var rows = new List<ConversationSummary>();
        foreach (var file in Directory.EnumerateFiles(_dir, "*.json"))
        {
            try
            {
                var c = JsonSerializer.Deserialize<Conversation>(File.ReadAllText(file), Json);
                if (c is not null && !string.IsNullOrEmpty(c.Id))
                    rows.Add(new ConversationSummary(c.Id, c.Title, c.UpdatedAt, c.Messages.Count));
            }
            catch (JsonException) { /* skip a corrupt file */ }
        }
        return rows.OrderByDescending(r => r.UpdatedAt, StringComparer.Ordinal).ToList();
    }

    public bool Delete(string id)
    {
        var path = PathFor(id);
        if (path is null || !File.Exists(path)) return false;
        try { File.Delete(path); return true; } catch { return false; }
    }

    // Ids are 32-hex GUIDs; refuse anything else so an id from a URL can never escape the folder.
    private string? PathFor(string id)
        => IdPattern().IsMatch(id ?? string.Empty) ? Path.Combine(_dir, id + ".json") : null;

    private static string Now() => DateTimeOffset.UtcNow.ToString("o");

    private static string? Clean(string? title)
    {
        if (string.IsNullOrWhiteSpace(title)) return null;
        var t = title.Trim().ReplaceLineEndings(" ");
        return t.Length > 80 ? t[..80].TrimEnd() + "…" : t;
    }

    [GeneratedRegex("^[a-f0-9]{32}$")]
    private static partial Regex IdPattern();
}

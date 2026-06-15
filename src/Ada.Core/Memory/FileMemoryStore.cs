using System.Text;

namespace Ada.Core;

/// <summary>
/// File-based memory: one markdown file per fact under <c>%APPDATA%\Ada\memory</c>, a <c>MEMORY.md</c>
/// index regenerated on every change, and an FTS5 index for recall. The files are the source of
/// truth — you can open, edit, or delete any of them by hand and Ada stays consistent.
/// </summary>
public sealed class FileMemoryStore : IMemoryStore, IDisposable
{
    private readonly string _dir;
    private readonly string _indexPath;
    private readonly MemoryIndex _fts;

    public FileMemoryStore(string? dir = null)
    {
        _dir = dir ?? Path.Combine(AdaPaths.DataDir, "memory");
        Directory.CreateDirectory(_dir);
        _indexPath = Path.Combine(_dir, "MEMORY.md");
        _fts = new MemoryIndex(Path.Combine(_dir, "memory.db"));
        Reindex();
    }

    public MemoryEntry Remember(string name, string description, MemoryType type, string body)
    {
        var entry = new MemoryEntry(Slug(name), description.Trim(), type, body.Trim());
        File.WriteAllText(FileFor(entry.Name), entry.ToMarkdown());
        Reindex();
        return entry;
    }

    public bool Forget(string name)
    {
        var path = FileFor(Slug(name));
        if (!File.Exists(path)) return false;
        File.Delete(path);
        Reindex();
        return true;
    }

    public MemoryEntry? Get(string name)
    {
        var path = FileFor(Slug(name));
        return File.Exists(path) ? Parse(File.ReadAllText(path)) : null;
    }

    public IReadOnlyList<MemoryEntry> List() => Files().Select(f => Parse(File.ReadAllText(f))).ToList();

    public IReadOnlyList<MemoryRecallHit> Recall(string query, int limit = 5) => _fts.Search(query, limit);

    public string IndexMarkdown() => File.Exists(_indexPath) ? File.ReadAllText(_indexPath) : string.Empty;

    public void Dispose() => _fts.Dispose();

    private void Reindex()
    {
        var entries = List();
        _fts.Rebuild(entries);

        var sb = new StringBuilder("# Memory index\n\n");
        foreach (var e in entries.OrderBy(e => e.Name, StringComparer.OrdinalIgnoreCase))
            sb.AppendLine($"- [{e.Name}]({e.Name}.md) — {e.Description}");
        File.WriteAllText(_indexPath, sb.ToString());
    }

    private IEnumerable<string> Files() =>
        Directory.EnumerateFiles(_dir, "*.md")
            .Where(f => !string.Equals(Path.GetFileName(f), "MEMORY.md", StringComparison.OrdinalIgnoreCase));

    private string FileFor(string slug) => Path.Combine(_dir, slug + ".md");

    private static string Slug(string name)
    {
        var slug = string.Concat(name.Trim().ToLowerInvariant().Select(c => char.IsLetterOrDigit(c) ? c : '-')).Trim('-');
        while (slug.Contains("--")) slug = slug.Replace("--", "-");
        return slug.Length == 0 ? "memory" : slug;
    }

    private static MemoryEntry Parse(string md)
    {
        string name = string.Empty, description = string.Empty, typeStr = "reference", body = md.Trim();
        if (md.StartsWith("---", StringComparison.Ordinal))
        {
            var end = md.IndexOf("---", 3, StringComparison.Ordinal);
            if (end > 0)
            {
                foreach (var raw in md[3..end].Split('\n'))
                {
                    var line = raw.Trim();
                    if (line.StartsWith("name:", StringComparison.OrdinalIgnoreCase)) name = line[5..].Trim();
                    else if (line.StartsWith("description:", StringComparison.OrdinalIgnoreCase)) description = line[12..].Trim();
                    else if (line.StartsWith("type:", StringComparison.OrdinalIgnoreCase)) typeStr = line[5..].Trim();
                }
                body = md[(end + 3)..].Trim();
            }
        }
        Enum.TryParse<MemoryType>(typeStr, ignoreCase: true, out var type);
        return new MemoryEntry(name, description, type, body);
    }
}

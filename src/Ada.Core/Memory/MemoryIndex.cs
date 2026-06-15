using System.Text.RegularExpressions;
using Microsoft.Data.Sqlite;

namespace Ada.Core;

/// <summary>
/// The recall index: SQLite FTS5 full-text search over memory (spec §9.5) — local, fast, zero extra
/// infrastructure, and rebuildable from the files at any time. Not a vector database.
/// </summary>
public sealed partial class MemoryIndex : IDisposable
{
    private readonly SqliteConnection _conn;

    public MemoryIndex(string dbPath)
    {
        _conn = new SqliteConnection($"Data Source={dbPath}");
        _conn.Open();
        Execute("CREATE VIRTUAL TABLE IF NOT EXISTS memory_fts USING fts5(name, description, body);");
    }

    /// <summary>Replaces the index with the current set of memories (cheap at memory scale).</summary>
    public void Rebuild(IEnumerable<MemoryEntry> entries)
    {
        Execute("DELETE FROM memory_fts;");
        using var tx = _conn.BeginTransaction();
        foreach (var e in entries)
        {
            using var cmd = _conn.CreateCommand();
            cmd.Transaction = tx;
            cmd.CommandText = "INSERT INTO memory_fts(name, description, body) VALUES(@n, @d, @b);";
            cmd.Parameters.AddWithValue("@n", e.Name);
            cmd.Parameters.AddWithValue("@d", e.Description);
            cmd.Parameters.AddWithValue("@b", e.Body);
            cmd.ExecuteNonQuery();
        }
        tx.Commit();
    }

    public IReadOnlyList<MemoryRecallHit> Search(string query, int limit)
    {
        var match = ToMatchQuery(query);
        if (match.Length == 0) return [];

        using var cmd = _conn.CreateCommand();
        cmd.CommandText =
            "SELECT name, description, snippet(memory_fts, 2, '[', ']', '…', 12) " +
            "FROM memory_fts WHERE memory_fts MATCH @q ORDER BY rank LIMIT @l;";
        cmd.Parameters.AddWithValue("@q", match);
        cmd.Parameters.AddWithValue("@l", limit);

        using var reader = cmd.ExecuteReader();
        var hits = new List<MemoryRecallHit>();
        while (reader.Read())
            hits.Add(new MemoryRecallHit(reader.GetString(0), reader.GetString(1), reader.GetString(2)));
        return hits;
    }

    public void Dispose() => _conn.Dispose();

    private void Execute(string sql)
    {
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = sql;
        cmd.ExecuteNonQuery();
    }

    // Reduce a free-text query to safe FTS5 bareword tokens OR'd together — avoids syntax errors.
    private static string ToMatchQuery(string query)
    {
        var words = WordRegex().Matches(query.ToLowerInvariant())
            .Select(m => m.Value)
            .Where(w => w.Length > 1)
            .Distinct()
            .ToList();
        return words.Count == 0 ? string.Empty : string.Join(" OR ", words);
    }

    [GeneratedRegex("[a-z0-9]+")]
    private static partial Regex WordRegex();
}

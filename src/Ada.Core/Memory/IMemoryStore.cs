namespace Ada.Core;

/// <summary>
/// Ada's durable memory (spec §9). Each fact is a readable file under <c>%APPDATA%\Ada\memory</c>
/// with a <c>MEMORY.md</c> index; recall is SQLite FTS5 over the bodies (not vectors, §9.5). Memory
/// is inspectable and erasable — nothing enters it silently.
/// </summary>
public interface IMemoryStore
{
    MemoryEntry Remember(string name, string description, MemoryType type, string body);
    bool Forget(string name);
    MemoryEntry? Get(string name);
    IReadOnlyList<MemoryEntry> List();
    IReadOnlyList<MemoryRecallHit> Recall(string query, int limit = 5);

    /// <summary>The <c>MEMORY.md</c> index content (one line per memory) — always loaded into context.</summary>
    string IndexMarkdown();
}

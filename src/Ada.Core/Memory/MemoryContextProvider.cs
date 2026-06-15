using System.Text;

namespace Ada.Core;

/// <summary>Builds the extra context Ada should see for a turn (memory, user model, …).</summary>
public interface ITurnContext
{
    Task<string?> BuildAsync(string userMessage, CancellationToken ct = default);
}

/// <summary>
/// How memory reaches the model (spec §9.2): the <c>USER.md</c> model and the <c>MEMORY.md</c> index
/// are always present, and the most relevant memories for this turn are recalled (FTS5) and folded in.
/// Returned as a system-context string the engine prepends to the turn.
/// </summary>
public sealed class MemoryContextProvider(IMemoryStore store, UserModel user, int recallLimit = 5) : ITurnContext
{
    public Task<string?> BuildAsync(string userMessage, CancellationToken ct = default)
    {
        var sb = new StringBuilder();

        var userModel = user.Read();
        if (!string.IsNullOrWhiteSpace(userModel))
            sb.AppendLine("## What you know about the user (USER.md)").AppendLine(userModel.Trim()).AppendLine();

        var index = store.IndexMarkdown();
        if (HasEntries(index))
            sb.AppendLine("## Your memory index — recall any of these on demand").AppendLine(index.Trim()).AppendLine();

        var hits = store.Recall(userMessage, recallLimit);
        if (hits.Count > 0)
        {
            sb.AppendLine("## Memories relevant to this turn");
            foreach (var hit in hits)
            {
                var entry = store.Get(hit.Name);
                sb.AppendLine($"- **{hit.Name}**: {(entry?.Body ?? hit.Description)}");
            }
        }

        var text = sb.ToString().Trim();
        return Task.FromResult<string?>(text.Length == 0 ? null : text);
    }

    private static bool HasEntries(string index) =>
        index.Contains("- [", StringComparison.Ordinal);
}

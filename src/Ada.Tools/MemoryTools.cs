using System.ComponentModel;
using Ada.Core;

namespace Ada.Tools;

/// <summary>
/// Ada's memory tools (spec §9). Remembering is a <em>visible</em> act — it writes an inspectable
/// file and is audited — but it isn't approval-gated: it's Ada's own house, not your files.
/// </summary>
public sealed class MemoryTools(IMemoryStore store, IAuditLog audit)
{
    [Description("Remember a durable fact about the user, their projects, or people. Writes an inspectable memory file.")]
    public async Task<string> Remember(
        [Description("A short name for the memory, e.g. 'accountant-tunde'.")] string name,
        [Description("A one-line description used for recall.")] string description,
        [Description("One of: user, feedback, project, reference.")] string type,
        [Description("The fact to remember.")] string content)
    {
        var memoryType = Enum.TryParse<MemoryType>(type, ignoreCase: true, out var t) ? t : MemoryType.Reference;
        var entry = store.Remember(name, description, memoryType, content);
        await audit.RecordAsync(new AuditEntry("remember", entry.Name, RiskTier.Low, "executed"));
        return $"Remembered '{entry.Name}'. You can open or edit it any time.";
    }

    [Description("Recall memories relevant to a query.")]
    public async Task<string> Recall([Description("What to look for.")] string query)
    {
        var hits = store.Recall(query, 5);
        await audit.RecordAsync(new AuditEntry("recall", query, RiskTier.ReadOnly, "executed"));
        return hits.Count == 0
            ? "No matching memories."
            : string.Join("\n", hits.Select(h => $"- {h.Name}: {h.Description}"));
    }

    [Description("Forget (delete) a memory by name.")]
    public async Task<string> Forget([Description("The memory name to delete.")] string name)
    {
        var ok = store.Forget(name);
        await audit.RecordAsync(new AuditEntry("forget", name, RiskTier.Low, ok ? "executed" : "not-found"));
        return ok ? $"Forgot '{name}'." : $"No memory named '{name}'.";
    }
}

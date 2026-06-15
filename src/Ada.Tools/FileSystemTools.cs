using System.ComponentModel;
using Ada.Core;

namespace Ada.Tools;

/// <summary>
/// Ada's hands on the filesystem. Reads are ungated; every mutation is <em>scope-checked first</em>
/// (so it's blocked outside the allowed roots even if later approved), then approval-gated, then
/// audited. The gate lives inside each method — the model cannot route around it.
/// </summary>
public sealed class FileSystemTools(ToolContext ctx)
{
    [Description("Read a UTF-8 text file and return its contents.")]
    public async Task<string> ReadFile([Description("Path to the file.")] string path)
    {
        string full;
        try { full = ctx.Scope.ResolveForRead(path); }
        catch (ScopeViolationException ex) { await Audit("read_file", path, RiskTier.ReadOnly, "blocked", ex.Message); return $"Blocked: {ex.Message}"; }

        if (!File.Exists(full)) { await Audit("read_file", full, RiskTier.ReadOnly, "not-found"); return $"No such file: {full}"; }
        var text = await File.ReadAllTextAsync(full);
        await Audit("read_file", full, RiskTier.ReadOnly, "executed");
        return text;
    }

    [Description("List the entries in a directory.")]
    public async Task<string> ListDirectory([Description("Path to the directory.")] string path)
    {
        string full;
        try { full = ctx.Scope.ResolveForRead(path); }
        catch (ScopeViolationException ex) { return $"Blocked: {ex.Message}"; }

        if (!Directory.Exists(full)) return $"No such directory: {full}";
        var entries = Directory.EnumerateFileSystemEntries(full).Select(Path.GetFileName);
        await Audit("list_directory", full, RiskTier.ReadOnly, "executed");
        return string.Join('\n', entries);
    }

    [Description("Create or overwrite a text file. Requires approval.")]
    public async Task<string> WriteFile(
        [Description("Path to the file.")] string path,
        [Description("The text content to write.")] string content)
    {
        string full;
        try { full = ctx.Scope.ResolveForWrite(path); }
        catch (ScopeViolationException ex) { await Audit("write_file", path, RiskTier.Low, "blocked", ex.Message); return $"Blocked: {ex.Message}"; }

        var request = new ApprovalRequest("write_file", RiskTier.Low, $"Write {content.Length} characters to a file", full);
        if (!await ctx.GateAsync(request)) { await Audit("write_file", full, RiskTier.Low, "denied"); return "Denied by the user."; }

        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        await File.WriteAllTextAsync(full, content);
        await Audit("write_file", full, RiskTier.Low, "executed");
        return $"Wrote {content.Length} characters to {full}.";
    }

    [Description("Delete a file. Requires approval.")]
    public async Task<string> DeleteFile([Description("Path to the file.")] string path)
    {
        string full;
        try { full = ctx.Scope.ResolveForWrite(path); }
        catch (ScopeViolationException ex) { await Audit("delete_file", path, RiskTier.Medium, "blocked", ex.Message); return $"Blocked: {ex.Message}"; }

        var request = new ApprovalRequest("delete_file", RiskTier.Medium, "Delete a file", full);
        if (!await ctx.GateAsync(request)) { await Audit("delete_file", full, RiskTier.Medium, "denied"); return "Denied by the user."; }

        if (File.Exists(full)) File.Delete(full);
        await Audit("delete_file", full, RiskTier.Medium, "executed");
        return $"Deleted {full}.";
    }

    private Task Audit(string tool, string target, RiskTier tier, string outcome, string? detail = null)
        => ctx.Audit.RecordAsync(new AuditEntry(tool, target, tier, outcome, detail));
}

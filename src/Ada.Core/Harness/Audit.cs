using System.Text.Json;

namespace Ada.Core;

/// <summary>One line in Ada's audit trail: what she did, to what, at what risk, and how it turned out.</summary>
public sealed record AuditEntry(string Tool, string Target, RiskTier Tier, string Outcome, string? Detail = null)
{
    public string TimestampUtc { get; init; } = DateTime.UtcNow.ToString("O");
}

public interface IAuditLog
{
    Task RecordAsync(AuditEntry entry, CancellationToken ct = default);
    IReadOnlyList<AuditEntry> Recent(int count = 50);
}

/// <summary>
/// Append-only JSONL audit at <c>%APPDATA%\Ada\audit.jsonl</c>. Every action Ada takes and every
/// approval outcome lands here — nothing is hidden (spec §8.5). The file is the source of truth; a
/// small in-memory tail is kept for quick display.
/// </summary>
public sealed class FileAuditLog : IAuditLog
{
    private readonly string _path;
    private readonly List<AuditEntry> _recent = new();
    private readonly SemaphoreSlim _gate = new(1, 1);

    public FileAuditLog(string? path = null)
        => _path = path ?? Path.Combine(AdaPaths.EnsureDataDir(), "audit.jsonl");

    public async Task RecordAsync(AuditEntry entry, CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            _recent.Add(entry);
            await File.AppendAllTextAsync(_path, JsonSerializer.Serialize(entry) + Environment.NewLine, ct).ConfigureAwait(false);
        }
        finally { _gate.Release(); }
    }

    public IReadOnlyList<AuditEntry> Recent(int count = 50) => _recent.TakeLast(count).ToList();
}

/// <summary>In-memory audit for tests.</summary>
public sealed class InMemoryAuditLog : IAuditLog
{
    private readonly List<AuditEntry> _entries = new();
    public Task RecordAsync(AuditEntry entry, CancellationToken ct = default) { _entries.Add(entry); return Task.CompletedTask; }
    public IReadOnlyList<AuditEntry> Recent(int count = 50) => _entries.TakeLast(count).ToList();
}

using System.Text.Json;
using System.Text.Json.Serialization;

namespace Ada.Core;

/// <summary>Persists the narrow standing grants that unattended runs may use.</summary>
public sealed class GrantStore
{
    private static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() },
    };

    private readonly string _path;

    public GrantStore(string? path = null) => _path = path ?? Path.Combine(AdaPaths.DataDir, "grants.json");

    public List<StandingGrant> Load()
    {
        if (!File.Exists(_path)) return [];
        try { return JsonSerializer.Deserialize<List<StandingGrant>>(File.ReadAllText(_path), Json) ?? []; }
        catch (JsonException) { return []; }
    }

    public void Save(IEnumerable<StandingGrant> grants)
    {
        AdaPaths.EnsureDataDir();
        File.WriteAllText(_path, JsonSerializer.Serialize(grants, Json));
    }
}

/// <summary>
/// The approval handler for unattended runs (spec §12.2): read-only by default. Every mutation is
/// denied — i.e. queued for the user to review later — unless a narrow standing grant covers that
/// exact tool up to its tier. An unattended job can therefore never quietly escalate.
/// </summary>
public sealed class UnattendedApprovalHandler(IReadOnlyList<StandingGrant> grants) : IApprovalHandler
{
    public Task<ApprovalDecision> RequestApprovalAsync(ApprovalRequest request, CancellationToken ct = default)
    {
        if (request.Tier == RiskTier.ReadOnly)
            return Task.FromResult(ApprovalDecision.Approve());

        var covered = grants.Any(g =>
            string.Equals(g.Tool, request.Tool, StringComparison.OrdinalIgnoreCase) && request.Tier <= g.MaxTier);
        return Task.FromResult(covered ? ApprovalDecision.Approve() : ApprovalDecision.Denied);
    }
}

/// <summary>One switch that pauses every job (spec §12). Persisted, so it survives restarts.</summary>
public sealed class KillSwitch
{
    private readonly string _path;

    public KillSwitch(string? path = null) => _path = path ?? Path.Combine(AdaPaths.DataDir, "jobs.paused");

    public bool Paused
    {
        get => File.Exists(_path);
        set
        {
            AdaPaths.EnsureDataDir();
            if (value) File.WriteAllText(_path, "paused");
            else if (File.Exists(_path)) File.Delete(_path);
        }
    }
}

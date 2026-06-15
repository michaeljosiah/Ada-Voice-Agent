namespace Ada.Core;

/// <summary>
/// The four risk tiers that decide whether an action is gated and how loudly (spec §8.2). The model
/// can never bypass a gate: gating lives inside the tool, not in a prompt the model could ignore.
/// </summary>
public enum RiskTier
{
    /// <summary>Reads only. Never gated — listing a folder or reading a file harms nothing.</summary>
    ReadOnly = 0,

    /// <summary>A contained mutation inside an allowed root: create/overwrite a file. Simple approval.</summary>
    Low = 1,

    /// <summary>A heavier mutation: delete, move, or run a shell command. Prominent approval.</summary>
    Medium = 2,

    /// <summary>Destructive, system-touching, or egress. Strong warning; never auto-approved.</summary>
    High = 3,
}

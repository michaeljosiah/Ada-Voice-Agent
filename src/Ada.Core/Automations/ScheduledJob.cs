namespace Ada.Core;

/// <summary>Where a job's result is delivered (spec §12.3).</summary>
public enum DeliveryTarget { Note, Toast, Chat }

/// <summary>A narrow, revocable standing grant that lets unattended runs perform one gated tool up to
/// a given tier without prompting (spec §12.2). Everything else stays read-only / queued.</summary>
public sealed record StandingGrant(string Tool, RiskTier MaxTier);

/// <summary>
/// A scheduled job (spec §12). Runs its <see cref="Prompt"/> through Ada on a <see cref="Cron"/>
/// schedule and delivers the result. Unattended runs are read-only by default and local by default
/// (cloud is per-job opt-in).
/// </summary>
public sealed record ScheduledJob(
    string Id,
    string Name,
    string Cron,
    string Prompt,
    DeliveryTarget Delivery = DeliveryTarget.Note,
    bool Enabled = true,
    bool CloudOptIn = false,
    DateTime? LastRunUtc = null);

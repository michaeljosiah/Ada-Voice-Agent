using System.Text;
using Cronos;

namespace Ada.Core;

/// <summary>
/// Runs due jobs (spec §12.5). A job is due when its next cron occurrence after its last run has
/// passed — so a job missed while Ada was closed catches up on the next check. Every autonomous run
/// is tagged in the audit log, and the kill switch pauses everything.
/// </summary>
public sealed class JobRunner(JobStore store, IDeliveryService delivery, IAuditLog audit, KillSwitch kill)
{
    public async Task<int> RunDueAsync(DateTime nowUtc, IAdaEngine engine, CancellationToken ct = default)
    {
        if (kill.Paused) return 0;

        var due = store.Load().Where(j => j.Enabled && IsDue(j, nowUtc)).ToList();
        foreach (var job in due)
        {
            var reply = await Collect(engine, job.Prompt, ct);
            await delivery.DeliverAsync(job, reply, ct);
            await audit.RecordAsync(new AuditEntry("job", job.Name, RiskTier.Low, "ran (autonomous)", job.Delivery.ToString()));
            store.Upsert(job with { LastRunUtc = nowUtc });
        }
        return due.Count;
    }

    /// <summary>True when the job's next scheduled time after its last run is at or before now.</summary>
    public static bool IsDue(ScheduledJob job, DateTime nowUtc)
    {
        CronExpression expression;
        try { expression = CronExpression.Parse(job.Cron); }
        catch { return false; }

        var from = job.LastRunUtc ?? nowUtc; // a brand-new job first fires at its next occurrence, not immediately
        var next = expression.GetNextOccurrence(from, TimeZoneInfo.Utc);
        return next is not null && next <= nowUtc;
    }

    private static async Task<string> Collect(IAdaEngine engine, string prompt, CancellationToken ct)
    {
        var sb = new StringBuilder();
        await foreach (var chunk in engine.RespondAsync(new AdaRequest(prompt), ct))
            sb.Append(chunk.Text);
        return sb.ToString();
    }
}

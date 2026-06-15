using System.ComponentModel;
using Ada.Core;

namespace Ada.Tools;

/// <summary>Ada's scheduling tools (spec §12): turn a natural-language schedule into a recurring job.</summary>
public sealed class ScheduleTools(JobStore store, IAuditLog audit)
{
    [Description("Schedule a recurring job. The schedule is natural language ('every weekday at 8') or a cron expression.")]
    public async Task<string> ScheduleJob(
        [Description("A short name for the job.")] string name,
        [Description("When to run, e.g. 'every weekday at 8' or '0 8 * * 1-5'.")] string schedule,
        [Description("What Ada should do on each run.")] string prompt,
        [Description("Delivery target: note, toast, or chat.")] string delivery = "note")
    {
        var cron = NlCron.ToCron(schedule);
        if (cron is null)
            return $"I couldn't turn '{schedule}' into a schedule. Try 'every weekday at 8' or a cron expression.";

        var target = Enum.TryParse<DeliveryTarget>(delivery, ignoreCase: true, out var d) ? d : DeliveryTarget.Note;
        store.Upsert(new ScheduledJob(Guid.NewGuid().ToString("n"), name, cron, prompt, target));
        await audit.RecordAsync(new AuditEntry("schedule_job", name, RiskTier.Low, "executed", cron));
        return $"Scheduled '{name}' ({cron}) — runs {schedule}, delivers to {target}.";
    }

    [Description("List the scheduled jobs.")]
    public Task<string> ListJobs()
    {
        var jobs = store.Load();
        return Task.FromResult(jobs.Count == 0
            ? "No scheduled jobs."
            : string.Join("\n", jobs.Select(j => $"- {j.Name} ({j.Cron}) -> {j.Delivery}{(j.Enabled ? string.Empty : " [disabled]")}")));
    }
}

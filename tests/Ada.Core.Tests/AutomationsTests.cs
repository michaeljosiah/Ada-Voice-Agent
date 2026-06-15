using Ada.Core;

namespace Ada.Core.Tests;

public class AutomationsTests
{
    [Theory]
    [InlineData("every weekday at 8", "0 8 * * 1-5")]
    [InlineData("every day at 8am", "0 8 * * *")]
    [InlineData("every friday at 5pm", "0 17 * * 5")]
    [InlineData("every 15 minutes", "*/15 * * * *")]
    [InlineData("0 8 * * 1-5", "0 8 * * 1-5")] // raw cron passes through
    public void NlCron_translates_common_phrases(string text, string expected)
        => Assert.Equal(expected, NlCron.ToCron(text));

    [Fact]
    public void NlCron_returns_null_for_gibberish()
        => Assert.Null(NlCron.ToCron("sometime soonish maybe"));

    [Fact]
    public void Overdue_job_is_due_and_fresh_job_is_not()
    {
        var now = DateTime.UtcNow;
        var overdue = new ScheduledJob("1", "j", "* * * * *", "do", LastRunUtc: now.AddMinutes(-5));
        var fresh = overdue with { LastRunUtc = now };

        Assert.True(JobRunner.IsDue(overdue, now));
        Assert.False(JobRunner.IsDue(fresh, now));
    }

    [Fact]
    public async Task RunDue_runs_a_due_job_delivers_and_audits()
    {
        var dir = Directory.CreateTempSubdirectory("ada_jobs_").FullName;
        try
        {
            var store = new JobStore(Path.Combine(dir, "jobs.json"));
            var job = new ScheduledJob("1", "morning-brief", "* * * * *", "brief me", DeliveryTarget.Note, LastRunUtc: DateTime.UtcNow.AddMinutes(-5));
            store.Upsert(job);

            var delivery = new NoteDeliveryService(dir);
            var audit = new InMemoryAuditLog();
            var runner = new JobRunner(store, delivery, audit, new KillSwitch(Path.Combine(dir, "paused")));

            var count = await runner.RunDueAsync(DateTime.UtcNow, new EchoEngine());

            Assert.Equal(1, count);
            Assert.True(File.Exists(delivery.PathFor(job)));
            Assert.Contains("brief me", await File.ReadAllTextAsync(delivery.PathFor(job)));
            Assert.Contains(audit.Recent(), e => e.Tool == "job" && e.Outcome.Contains("autonomous"));
            Assert.NotNull(store.Load().Single().LastRunUtc);
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public async Task Kill_switch_pauses_all_jobs()
    {
        var dir = Directory.CreateTempSubdirectory("ada_jobs_").FullName;
        try
        {
            var store = new JobStore(Path.Combine(dir, "jobs.json"));
            store.Upsert(new ScheduledJob("1", "j", "* * * * *", "x", LastRunUtc: DateTime.UtcNow.AddMinutes(-5)));
            var kill = new KillSwitch(Path.Combine(dir, "paused")) { Paused = true };
            var runner = new JobRunner(store, new NoteDeliveryService(dir), new InMemoryAuditLog(), kill);

            Assert.Equal(0, await runner.RunDueAsync(DateTime.UtcNow, new EchoEngine()));
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public async Task Unattended_runs_are_read_only_unless_a_grant_covers_the_tool()
    {
        var noGrants = new UnattendedApprovalHandler([]);
        Assert.True((await noGrants.RequestApprovalAsync(new ApprovalRequest("read_file", RiskTier.ReadOnly, "read", "x"))).Approved);
        Assert.False((await noGrants.RequestApprovalAsync(new ApprovalRequest("write_file", RiskTier.Low, "write", "x"))).Approved);

        var granted = new UnattendedApprovalHandler([new StandingGrant("write_file", RiskTier.Low)]);
        Assert.True((await granted.RequestApprovalAsync(new ApprovalRequest("write_file", RiskTier.Low, "write", "x"))).Approved);
        // a grant never widens past its tier:
        Assert.False((await granted.RequestApprovalAsync(new ApprovalRequest("write_file", RiskTier.High, "write", "x"))).Approved);
    }
}

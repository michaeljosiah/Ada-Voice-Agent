namespace Ada.Core;

/// <summary>Delivers a job's result to its target (spec §12.3).</summary>
public interface IDeliveryService
{
    Task DeliverAsync(ScheduledJob job, string content, CancellationToken ct = default);
}

/// <summary>
/// The headless-safe default: write the result to a note under <c>%APPDATA%\Ada\briefings</c>. Toast
/// and Chat targets fall back to a note when Ada is closed; the GUI surfaces them when it's open.
/// </summary>
public sealed class NoteDeliveryService(string? dir = null) : IDeliveryService
{
    private readonly string _dir = dir ?? Path.Combine(AdaPaths.DataDir, "briefings");

    public async Task DeliverAsync(ScheduledJob job, string content, CancellationToken ct = default)
    {
        Directory.CreateDirectory(_dir);
        await File.WriteAllTextAsync(PathFor(job), $"# {job.Name}\n\n{content}\n", ct);
    }

    public string PathFor(ScheduledJob job) => Path.Combine(_dir, Slug(job.Name) + ".md");

    private static string Slug(string name) =>
        string.Concat(name.Select(c => char.IsLetterOrDigit(c) ? c : '-'));
}

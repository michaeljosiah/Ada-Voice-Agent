using System.Text.Json;
using System.Text.Json.Serialization;

namespace Ada.Core;

/// <summary>Persists scheduled jobs as plain JSON at <c>%APPDATA%\Ada\jobs.json</c>.</summary>
public sealed class JobStore
{
    private static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() },
    };

    private readonly string _path;

    public JobStore(string? path = null) => _path = path ?? Path.Combine(AdaPaths.DataDir, "jobs.json");

    public List<ScheduledJob> Load()
    {
        if (!File.Exists(_path)) return [];
        try { return JsonSerializer.Deserialize<List<ScheduledJob>>(File.ReadAllText(_path), Json) ?? []; }
        catch (JsonException) { return []; }
    }

    public void Save(IEnumerable<ScheduledJob> jobs)
    {
        AdaPaths.EnsureDataDir();
        File.WriteAllText(_path, JsonSerializer.Serialize(jobs, Json));
    }

    public void Upsert(ScheduledJob job)
    {
        var jobs = Load();
        jobs.RemoveAll(j => j.Id == job.Id);
        jobs.Add(job);
        Save(jobs);
    }

    public void Remove(string idOrName)
    {
        var jobs = Load();
        jobs.RemoveAll(j => j.Id == idOrName || string.Equals(j.Name, idOrName, StringComparison.OrdinalIgnoreCase));
        Save(jobs);
    }
}

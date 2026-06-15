using System.Text.Json;

namespace Ada.Core;

/// <summary>
/// Tracks which skills are enabled. Defaults come from each skill's <see cref="ISkill.EnabledByDefault"/>;
/// the user's choices are persisted to <c>%APPDATA%\Ada\skills.json</c>. Toggling a skill changes only
/// this state — the agent is recomposed from it (spec §7.3).
/// </summary>
public sealed class SkillRegistry
{
    private readonly IReadOnlyList<ISkill> _all;
    private readonly string _statePath;

    public SkillRegistry(IEnumerable<ISkill> skills, string? statePath = null)
    {
        _all = skills.ToList();
        _statePath = statePath ?? Path.Combine(AdaPaths.DataDir, "skills.json");
    }

    public IReadOnlyList<ISkill> Available => _all;

    public IReadOnlyList<ISkill> Enabled
    {
        get
        {
            var names = EnabledNames();
            return _all.Where(s => names.Contains(s.Name)).ToList();
        }
    }

    public bool IsEnabled(string name) => EnabledNames().Contains(name);

    public void Enable(string name) => SetEnabled(name, true);
    public void Disable(string name) => SetEnabled(name, false);

    private void SetEnabled(string name, bool enabled)
    {
        if (_all.All(s => !string.Equals(s.Name, name, StringComparison.OrdinalIgnoreCase)))
            throw new ArgumentException($"Unknown skill '{name}'.");

        var names = EnabledNames();
        if (enabled) names.Add(name); else names.Remove(name);
        AdaPaths.EnsureDataDir();
        File.WriteAllText(_statePath, JsonSerializer.Serialize(names.ToArray()));
    }

    private HashSet<string> EnabledNames()
    {
        if (File.Exists(_statePath))
        {
            try
            {
                var saved = JsonSerializer.Deserialize<string[]>(File.ReadAllText(_statePath));
                if (saved is not null) return new HashSet<string>(saved, StringComparer.OrdinalIgnoreCase);
            }
            catch (JsonException) { /* fall back to defaults */ }
        }
        return new HashSet<string>(_all.Where(s => s.EnabledByDefault).Select(s => s.Name), StringComparer.OrdinalIgnoreCase);
    }
}

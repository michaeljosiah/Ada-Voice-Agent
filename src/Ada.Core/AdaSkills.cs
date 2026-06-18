using Microsoft.Agents.AI;

namespace Ada.Core;

/// <summary>
/// Bridges Ada to Microsoft Agent Framework's file-based skills (the agentskills.io spec): a folder per
/// skill under <see cref="AdaPaths.SkillsDir"/>, discovered by an <see cref="AgentSkillsProvider"/> that
/// gives the agent <c>load_skill</c> / <c>read_skill_resource</c> / <c>run_skill_script</c> with
/// progressive disclosure. Bundled <c>.py</c>/<c>.js</c> scripts run in the AIO sandbox via
/// <see cref="AioSkillScriptRunner"/> (python3 / node, by extension). Returns no provider when there are
/// no skills yet, so those tools
/// stay off the agent's surface until the user actually drops a skill in.
/// </summary>
public static class AdaSkills
{
    /// <summary>True if at least one skill folder (containing a <c>SKILL.md</c>) exists.</summary>
    public static bool Any()
    {
        if (!Directory.Exists(AdaPaths.SkillsDir)) return false;
        foreach (var sub in Directory.EnumerateDirectories(AdaPaths.SkillsDir))
            if (File.Exists(Path.Combine(sub, "SKILL.md"))) return true;
        return false;
    }

    /// <summary>A discovered file-based skill, for the Settings list.</summary>
    public sealed record SkillInfo(string Name, string Description, string? Compatibility, bool HasScripts);

    /// <summary>Lists the file-based skills under the skills dir (parsed from each SKILL.md frontmatter).</summary>
    public static IReadOnlyList<SkillInfo> List()
    {
        var result = new List<SkillInfo>();
        if (!Directory.Exists(AdaPaths.SkillsDir)) return result;

        foreach (var dir in Directory.EnumerateDirectories(AdaPaths.SkillsDir).OrderBy(d => d, StringComparer.OrdinalIgnoreCase))
        {
            var md = Path.Combine(dir, "SKILL.md");
            if (!File.Exists(md)) continue;

            var (name, desc, compat) = ParseFrontmatter(md, Path.GetFileName(dir));
            var scripts = Path.Combine(dir, "scripts");
            var hasScripts = Directory.Exists(scripts) &&
                (Directory.EnumerateFiles(scripts, "*.py").Any() || Directory.EnumerateFiles(scripts, "*.js").Any());
            result.Add(new SkillInfo(name, desc, compat, hasScripts));
        }
        return result;
    }

    // Minimal YAML-frontmatter reader: pulls the top-level name/description/compatibility between the
    // first pair of '---' fences. Enough for the Settings list — MAF does the authoritative parsing.
    private static (string name, string desc, string? compat) ParseFrontmatter(string path, string fallbackName)
    {
        string name = fallbackName, desc = string.Empty, compat = string.Empty;
        try
        {
            var lines = File.ReadAllLines(path);
            var start = Array.FindIndex(lines, l => l.Trim() == "---");
            if (start >= 0)
            {
                var end = Array.FindIndex(lines, start + 1, l => l.Trim() == "---");
                if (end > start)
                {
                    for (var i = start + 1; i < end; i++)
                    {
                        var line = lines[i];
                        if (line.Length > 0 && char.IsWhiteSpace(line[0])) continue; // skip nested (e.g. metadata:) keys
                        var idx = line.IndexOf(':');
                        if (idx <= 0) continue;
                        var key = line[..idx].Trim().ToLowerInvariant();
                        var val = line[(idx + 1)..].Trim().Trim('"', '\'');
                        if (key == "name" && val.Length > 0) name = val;
                        else if (key == "description") desc = val;
                        else if (key == "compatibility") compat = val;
                    }
                }
            }
        }
        catch { /* unreadable → fallbacks */ }
        return (name, desc, string.IsNullOrWhiteSpace(compat) ? null : compat);
    }

    /// <summary>The file-based skills provider, or <see langword="null"/> when no skills are present.</summary>
    public static AgentSkillsProvider? BuildProvider(SandboxSession session)
    {
        if (!Any()) return null;

        var runner = new AioSkillScriptRunner(session);
        // The sandbox runner picks the interpreter by extension: .py → python3, .js → node.
        var fileOptions = new AgentFileSkillsSourceOptions { AllowedScriptExtensions = [".py", ".js"] };
        return new AgentSkillsProvider(AdaPaths.EnsureSkillsDir(), runner.RunAsync, fileOptions, options: null, loggerFactory: null);
    }
}

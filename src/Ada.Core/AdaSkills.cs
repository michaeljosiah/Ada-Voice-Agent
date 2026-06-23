using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;

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

    /// <summary>Builds MAF progressive-discovery skills from file skills plus mounted sandbox MCP tools.</summary>
    public static AgentSkillsProvider? BuildProvider(
        SandboxSession session,
        IReadOnlyList<AITool>? directTools = null,
        IReadOnlyList<AITool>? sandboxTools = null,
        ILoggerFactory? loggerFactory = null)
    {
        var hasFileSkills = Any();
        var toolSkillGroups = BuildToolSkillGroups(directTools, sandboxTools, loggerFactory).ToList();
        if (!hasFileSkills && toolSkillGroups.Count == 0) return null;

        var builder = new AgentSkillsProviderBuilder();
        if (hasFileSkills)
        {
            var runner = new AioSkillScriptRunner(session);
            // The sandbox runner picks the interpreter by extension: .py → python3, .js → node.
            var fileOptions = new AgentFileSkillsSourceOptions { AllowedScriptExtensions = [".py", ".js"] };
            builder.UseFileSkill(AdaPaths.EnsureSkillsDir(), fileOptions, runner.RunAsync);
        }
        if (toolSkillGroups.Count > 0)
            builder.UseSkills(toolSkillGroups);
        builder.UsePromptTemplate(SkillsPrompt);
        if (loggerFactory is not null)
            builder.UseLoggerFactory(loggerFactory);

        return builder.Build();
    }

    private const string SkillsPrompt = """
        You have access to skills containing action capabilities and specialized knowledge.

        <available_skills>
        {skills}
        </available_skills>

        Use skills only when the user asks Ada to perform an action, inspect local/sandbox state, run code or commands, search files/web, use memory, email, or scheduling.
        For ordinary conversation, do not load or run skills.

        When a task requires a skill, follow these steps in exact order:
        - Use `load_skill` to retrieve the most relevant skill's instructions.
        - Choose the exact script/resource from the loaded skill content.
        {resource_instructions}
        {script_instructions}
        - After every `run_skill_script` call, inspect the returned result and answer the user with the result or a concise summary.
        - Do not stop after saying you will run a script. The turn is not complete until you have reported what happened.
        - If a script returns an error, report the error plainly and suggest the next useful step. Do not hide the error behind a generic failure.
        """;

    private static IEnumerable<AgentSkill> BuildToolSkillGroups(
        IReadOnlyList<AITool>? directTools,
        IReadOnlyList<AITool>? sandboxTools,
        ILoggerFactory? loggerFactory)
    {
        var groups = new Dictionary<string, List<ToolEntry>>(StringComparer.Ordinal)
        {
            ["workspace"] = [],
            ["shell"] = [],
            ["code"] = [],
            ["web"] = [],
            ["memory"] = [],
            ["email"] = [],
            ["schedule"] = [],
            ["other"] = [],
        };
        var usedScripts = new HashSet<string>(StringComparer.Ordinal);

        AddTools(groups, usedScripts, directTools, "ada");
        AddTools(groups, usedScripts, sandboxTools, "sandbox");

        foreach (var (group, tools) in groups)
        {
            if (tools.Count == 0) continue;
            var (name, description, instructions) = GroupInfo(group);
            yield return new ToolGroupSkill(name, description, instructions, tools, loggerFactory);
        }
    }

    private static void AddTools(Dictionary<string, List<ToolEntry>> groups, HashSet<string> usedScripts, IReadOnlyList<AITool>? tools, string source)
    {
        if (tools is null) return;
        foreach (var tool in tools.OfType<AIFunction>())
        {
            var script = UniqueScriptName(tool.Name, source, usedScripts);
            groups[GroupFor(tool)].Add(new ToolEntry(script, source, tool));
        }
    }

    private static string GroupFor(AITool tool)
    {
        var text = (tool.Name + " " + tool.Description).ToLowerInvariant();
        if (Any(text, "email", "mail", "outlook")) return "email";
        if (Any(text, "schedule", "job", "remind", "calendar")) return "schedule";
        if (Any(text, "remember", "recall", "forget", "memory")) return "memory";
        if (Any(text, "browser", "web", "url", "http", "page", "website")) return "web";
        if (Any(text, "code", "script", "test", "build", "dotnet", "python", "node", "npm")) return "code";
        if (Any(text, "shell", "command", "terminal", "powershell", "bash", "process", "docker", "container")) return "shell";
        if (Any(text, "file", "folder", "directory", "path", "workspace", "repo", "repository", "read", "write", "list", "delete", "search", "find", "grep")) return "workspace";
        return "other";
    }

    private static (string Name, string Description, string Instructions) GroupInfo(string group) => group switch
    {
        "workspace" => ("workspace-tools", "Inspect, search, read, write, list, create, and delete files and directories in Ada's workspace or sandbox.", """
            Use this skill for repository/workspace file and directory tasks.

            Path rules:
            - When the user says "my workspace", "the workspace", "workspace root", "repo", or "repository", use Ada's workspace root.
            - In the sandbox, Ada's workspace root is `/home/gem/workspace`.
            - Do not use `/` for workspace requests.
            - Do not ask for clarification for generic workspace/repo requests; default to `/home/gem/workspace`.
            - If the user gives a relative path, resolve it under `/home/gem/workspace`.
            - For "list files in my workspace", call the relevant file/list script with `/home/gem/workspace`.
            - After the script returns, report the actual returned entries. Do not describe the generic Linux filesystem unless the user explicitly asked for `/`.
            """),
        "shell" => ("shell-tools", "Run shell, terminal, command, process, Docker, or container actions through Ada's controlled tool layer.", "Use this skill only when the user asks to run commands or inspect the runtime environment."),
        "code" => ("code-tools", "Run code, scripts, builds, tests, and developer tooling in Ada's local or sandbox environment.", "Use this skill for code execution, test runs, builds, and script tasks."),
        "web" => ("web-tools", "Use web, browser, URL, page, and online lookup capabilities available to Ada.", "Use this skill when the user asks Ada to browse, fetch, or inspect web content."),
        "memory" => ("memory-tools", "Remember, recall, and forget user-provided memory intentionally.", "Use this skill only when the user asks Ada to remember, recall, or forget something."),
        "email" => ("email-tools", "Inspect and work with connected mail or Outlook accounts.", "Use this skill when the user asks about email or Outlook."),
        "schedule" => ("schedule-tools", "Create, list, and manage scheduled jobs or reminders.", "Use this skill for scheduling, reminders, and recurring local jobs."),
        _ => ("ada-tools", "Other Ada action tools available through progressive discovery.", "Use this skill only if no more specific skill group matches the requested action."),
    };

    private static string UniqueScriptName(string toolName, string source, HashSet<string> used)
    {
        var name = Sanitize(toolName);
        if (used.Add(name)) return name;

        name = Sanitize(source + "-" + toolName);
        if (used.Add(name)) return name;

        var baseName = name.Length > 58 ? name[..58].Trim('-') : name;
        var i = 2;
        while (true)
        {
            var suffix = "-" + i++;
            var unique = baseName.Length + suffix.Length <= 64
                ? baseName + suffix
                : baseName[..(64 - suffix.Length)].Trim('-') + suffix;
            if (used.Add(unique)) return unique;
        }
    }

    private static string Sanitize(string value)
    {
        var sb = new System.Text.StringBuilder();
        var lastHyphen = true;
        foreach (var c in value.ToLowerInvariant())
        {
            if (char.IsLetterOrDigit(c)) { sb.Append(c); lastHyphen = false; }
            else if (!lastHyphen) { sb.Append('-'); lastHyphen = true; }
        }
        var name = sb.ToString().Trim('-');
        if (name.Length == 0) name = "tool";
        return name.Length <= 64 ? name : name[..64].Trim('-');
    }

    private static bool Any(string value, params string[] needles)
        => needles.Any(n => value.Contains(n, StringComparison.Ordinal));
}

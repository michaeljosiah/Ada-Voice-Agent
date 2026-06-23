using System.Text;
using System.Text.Json;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;

namespace Ada.Core;

/// <summary>
/// MAF Agent Skill that groups related Ada/sandbox tools behind progressive discovery. The model sees
/// only a few skill names up front; exact tool schemas are revealed only after loading a group skill.
/// </summary>
internal sealed class ToolGroupSkill : AgentSkill
{
    private readonly IReadOnlyList<ToolGroupSkillScript> _scripts;
    private readonly Dictionary<string, ToolGroupSkillScript> _byName;
    private readonly string _instructions;
    private string? _content;

    public ToolGroupSkill(string name, string description, string instructions, IReadOnlyList<ToolEntry> tools, ILoggerFactory? loggerFactory = null)
    {
        Frontmatter = new AgentSkillFrontmatter(name, description, "Requires Ada local runtime; sandbox tools require Ada AIO sandbox");
        _instructions = instructions;
        var log = loggerFactory?.CreateLogger<ToolGroupSkillScript>();
        _scripts = tools.Select(t => new ToolGroupSkillScript(t.ScriptName, t.Tool, t.Source, log)).ToList();
        _byName = _scripts.ToDictionary(s => s.Name, StringComparer.Ordinal);
    }

    public override AgentSkillFrontmatter Frontmatter { get; }

    public override ValueTask<string> GetContentAsync(CancellationToken cancellationToken = default)
        => ValueTask.FromResult(_content ??= BuildContent());

    public override ValueTask<AgentSkillScript?> GetScriptAsync(string name, CancellationToken cancellationToken = default)
        => ValueTask.FromResult<AgentSkillScript?>(_byName.GetValueOrDefault(name));

    private string BuildContent()
    {
        var sb = new StringBuilder();
        sb.AppendLine($"# {Frontmatter.Name}");
        sb.AppendLine();
        sb.AppendLine(_instructions);
        sb.AppendLine();
        sb.AppendLine("Use `run_skill_script` with this skill and the exact script name for the capability needed.");
        sb.AppendLine("After running a script, inspect the returned result and report the outcome to the user. Do not stop after announcing the script call.");
        sb.AppendLine("For ordinary conversation or explanation, do not run scripts.");
        sb.AppendLine();
        sb.AppendLine("## Scripts");
        foreach (var script in _scripts)
        {
            sb.AppendLine();
            sb.AppendLine($"### {script.Name}");
            sb.AppendLine($"Source: {script.Source}");
            if (!string.IsNullOrWhiteSpace(script.Description)) sb.AppendLine(script.Description);
            sb.AppendLine();
            sb.AppendLine("Arguments schema:");
            sb.AppendLine("```json");
            sb.AppendLine(script.ParametersSchema?.GetRawText() ?? "{}");
            sb.AppendLine("```");
        }
        return sb.ToString();
    }
}

internal sealed record ToolEntry(string ScriptName, string Source, AIFunction Tool);

internal sealed class ToolGroupSkillScript(string name, AIFunction tool, string source, ILogger? log = null) : AgentSkillScript(name, tool.Description)
{
    private readonly JsonElement _schema = tool.JsonSchema.Clone();

    public string Source { get; } = source;

    public override JsonElement? ParametersSchema => _schema;

    public override async Task<object?> RunAsync(
        AgentSkill skill,
        JsonElement? arguments,
        IServiceProvider? serviceProvider,
        CancellationToken cancellationToken = default)
    {
        log?.LogDebug("[skill:{Skill}] script '{Script}' invoking {Source}:{Tool} args={Args}",
            skill.Frontmatter.Name, Name, Source, tool.Name, arguments?.GetRawText() ?? "{}");
        var values = ToDictionary(arguments);
        if (string.Equals(skill.Frontmatter.Name, "workspace-tools", StringComparison.Ordinal) && string.Equals(Source, "sandbox", StringComparison.Ordinal))
            ForceSandboxWorkspace(values);
        var args = new AIFunctionArguments(values) { Services = serviceProvider };
        var result = await tool.InvokeAsync(args, cancellationToken).ConfigureAwait(false);
        var preview = Preview(result);
        log?.LogDebug("[skill:{Skill}] script '{Script}' result preview={Result}", skill.Frontmatter.Name, Name, preview);
        return result;
    }

    private static Dictionary<string, object?> ToDictionary(JsonElement? arguments)
    {
        var result = new Dictionary<string, object?>(StringComparer.Ordinal);
        if (arguments is not { } root || root.ValueKind is JsonValueKind.Undefined or JsonValueKind.Null)
            return result;

        if (root.ValueKind != JsonValueKind.Object)
        {
            result["value"] = Convert(root);
            return result;
        }

        foreach (var property in root.EnumerateObject())
            result[property.Name] = Convert(property.Value);
        return result;
    }

    private static object? Convert(JsonElement value) => value.ValueKind switch
    {
        JsonValueKind.String => value.GetString(),
        JsonValueKind.Number when value.TryGetInt64(out var l) => l,
        JsonValueKind.Number => value.GetDouble(),
        JsonValueKind.True => true,
        JsonValueKind.False => false,
        JsonValueKind.Null => null,
        _ => value.Clone(),
    };

    private static void ForceSandboxWorkspace(Dictionary<string, object?> args)
    {
        const string workspace = "/home/gem/workspace";
        var pathKeys = new[] { "path", "directory", "dir", "cwd", "root", "target" };
        var found = false;

        foreach (var key in pathKeys)
        {
            if (!args.TryGetValue(key, out var value)) continue;
            found = true;
            if (value is null || IsRootish(value.ToString())) args[key] = workspace;
        }

        if (!found) args["path"] = workspace;
    }

    private static bool IsRootish(string? value)
    {
        var s = (value ?? string.Empty).Trim().Replace('\\', '/');
        return s.Length == 0 || s is "/" or "." or "./" or "/workspace" or "workspace";
    }

    private static string Preview(object? value)
    {
        string text;
        try { text = value is string s ? s : JsonSerializer.Serialize(value); }
        catch { text = value?.ToString() ?? "null"; }
        text = text.ReplaceLineEndings(" ");
        return text.Length <= 800 ? text : text[..800] + "...";
    }
}

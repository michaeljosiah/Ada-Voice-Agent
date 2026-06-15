using System.Text;
using Microsoft.Extensions.AI;

namespace Ada.Core;

/// <summary>The agent as composed from the persona plus the enabled skills.</summary>
public sealed record ComposedAgent(string Instructions, IReadOnlyList<AITool> Tools, IReadOnlyList<McpMount> Mounts);

/// <summary>
/// Folds the enabled skills into the agent (spec §7.3): each skill's instruction fragment is appended
/// to the persona, its tools are added, and its MCP mount (if any) is collected. This is the whole of
/// "enabling a skill adds its instructions + tools with no core change".
/// </summary>
public static class SkillComposer
{
    public static ComposedAgent Compose(Persona persona, IEnumerable<AITool> baseTools, IEnumerable<ISkill> enabledSkills)
    {
        var instructions = new StringBuilder(persona.Instructions);
        var tools = new List<AITool>(baseTools);
        var mounts = new List<McpMount>();

        foreach (var skill in enabledSkills)
        {
            if (!string.IsNullOrWhiteSpace(skill.InstructionFragment))
                instructions.AppendLine().AppendLine().AppendLine($"## Skill: {skill.Name}").AppendLine(skill.InstructionFragment);

            tools.AddRange(skill.Tools);
            if (skill.Mcp is not null)
                mounts.Add(skill.Mcp);
        }

        return new ComposedAgent(instructions.ToString(), tools, mounts);
    }
}

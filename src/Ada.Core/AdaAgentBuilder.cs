using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;

namespace Ada.Core;

/// <summary>
/// One place that constructs Ada's <see cref="ChatClientAgent"/>, so the text and voice surfaces build
/// the agent identically. When a file-based skills provider is present it is attached via
/// <see cref="ChatClientAgentOptions.AIContextProviders"/> (which also requires moving instructions and
/// tools onto <see cref="ChatOptions"/>); otherwise the simpler constructor is used unchanged.
/// </summary>
internal static class AdaAgentBuilder
{
    public static ChatClientAgent Build(IChatClient client, string instructions, IReadOnlyList<AITool> tools, AgentSkillsProvider? skills)
    {
        var toolList = tools.Count > 0 ? tools.ToList() : null;

        if (skills is null)
            return new ChatClientAgent(client, instructions: instructions, name: "Ada", tools: toolList);

        return new ChatClientAgent(client, new ChatClientAgentOptions
        {
            Name = "Ada",
            ChatOptions = new ChatOptions { Instructions = instructions, Tools = toolList },
            AIContextProviders = [skills],
        });
    }
}

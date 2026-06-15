using Ada.Core;
using Microsoft.Extensions.AI;

namespace Ada.Tools;

/// <summary>The Research skill (spec §7.3): web reach + a synthesis instruction.</summary>
public sealed class ResearchSkill(WebTools web) : ISkill
{
    public string Name => "research";
    public bool EnabledByDefault => true;

    public string? InstructionFragment =>
        "When researching: fetch the relevant sources, read them, and synthesize a concise answer. " +
        "Cite the URLs you used, and clearly separate what you found from what you inferred.";

    public IReadOnlyList<AITool> Tools =>
    [
        AIFunctionFactory.Create(web.WebFetch, "web_fetch"),
        AIFunctionFactory.Create(web.WebSearch, "web_search"),
    ];

    public McpMount? Mcp => null;
}

using Ada.Core;
using Microsoft.Extensions.AI;

namespace Ada.Tools;

/// <summary>
/// The external-service seam (spec §7.3 / §9.4), stubbed and disabled. When a finance/records backend
/// ships an MCP server, this skill mounts it — no core change. The instruction carries the product
/// rules: describe-not-prescribe, and amounts in their original currency, never converted.
/// </summary>
public sealed class FinanceRecordsSkill : ISkill
{
    public string Name => "finance-records";
    public bool EnabledByDefault => false; // off until an external server exists

    public string? InstructionFragment =>
        "You can discuss the user's finance/records when this skill is connected. Describe, never " +
        "prescribe — tell them what they committed and what they paid, never what to do with their " +
        "money. Always keep amounts in their original currency; never convert.";

    public IReadOnlyList<AITool> Tools => [];

    public McpMount? Mcp => new(
        Name: "finance-records",
        Transport: McpTransport.Http,
        Url: "https://mcp.example.com",
        IsEgress: true,
        GateMutatingTools: true);
}

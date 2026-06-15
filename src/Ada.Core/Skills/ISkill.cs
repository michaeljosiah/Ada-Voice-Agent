using Microsoft.Extensions.AI;

namespace Ada.Core;

public enum McpTransport { Stdio, Http }

/// <summary>
/// An external MCP server a skill mounts (spec §7.4). A non-local mount is an <em>egress channel</em>:
/// named, listed, logged, and — for a write-capable server — its tools are gated.
/// </summary>
public sealed record McpMount(
    string Name,
    McpTransport Transport,
    string? Command = null,
    IReadOnlyList<string>? Args = null,
    string? Url = null,
    bool IsEgress = false,
    bool GateMutatingTools = true);

/// <summary>
/// A skill (spec §7.3): a self-contained bundle of an instruction fragment, tools, and optionally an
/// MCP mount. Enabling a skill adds its instructions and tools to Ada with no core change.
/// </summary>
public interface ISkill
{
    string Name { get; }
    string? InstructionFragment { get; }
    IReadOnlyList<AITool> Tools { get; }
    McpMount? Mcp { get; }
    bool EnabledByDefault { get; }
}

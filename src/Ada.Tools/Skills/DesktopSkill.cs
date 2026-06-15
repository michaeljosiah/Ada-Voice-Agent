using Ada.Core;
using Microsoft.Extensions.AI;

namespace Ada.Tools;

/// <summary>
/// The Desktop skill (spec §7.3): a tidy-up instruction over Ada's always-on file and shell tools.
/// It demonstrates an instruction-only skill — the capabilities are the base tools; the skill shapes
/// how Ada uses them.
/// </summary>
public sealed class DesktopSkill : ISkill
{
    public string Name => "desktop";
    public bool EnabledByDefault => true;

    public string? InstructionFragment =>
        "You can tidy and organize the user's files and run commands on their machine. Propose the " +
        "moves in one plain line, ask before anything that writes or deletes, and stay within the " +
        "allowed folders. Prefer reversible operations.";

    public IReadOnlyList<AITool> Tools => [];
    public McpMount? Mcp => null;
}

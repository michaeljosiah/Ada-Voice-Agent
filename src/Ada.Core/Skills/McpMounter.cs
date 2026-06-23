using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.Client;

namespace Ada.Core;

/// <summary>
/// Mounts an external MCP server (spec §7.4): launches it, lists its tools, and — for a write-capable
/// server — wraps each tool in Ada's approval gate. Every mount is an egress channel, recorded in the
/// audit log. Clients are kept alive for the lifetime of the mounter so the tools remain callable.
/// </summary>
public sealed class McpMounter(IApprovalHandler approval, IAuditLog audit, ILogger<McpMounter>? log = null) : IAsyncDisposable
{
    private readonly List<McpClient> _clients = [];

    /// <summary>How long a single mounted MCP tool call may run before it's cancelled, so a wedged tool
    /// (e.g. an unresponsive sandbox) can't hang the agent turn forever. Generous enough for real work.</summary>
    private static readonly TimeSpan ToolTimeout = TimeSpan.FromSeconds(60);

    public async Task<IReadOnlyList<AITool>> MountAsync(McpMount mount, CancellationToken ct = default)
    {
        IClientTransport transport = mount.Transport switch
        {
            // A local server Ada launches as a child process (stdio framing).
            McpTransport.Stdio when !string.IsNullOrWhiteSpace(mount.Command) => new StdioClientTransport(new StdioClientTransportOptions
            {
                Name = mount.Name,
                Command = mount.Command!,
                Arguments = mount.Args?.ToArray(),
            }),
            // An already-running server reached over HTTP — e.g. the AIO sandbox's /mcp on loopback.
            // AutoDetect tries Streamable HTTP first, then falls back to SSE.
            McpTransport.Http when !string.IsNullOrWhiteSpace(mount.Url) => new HttpClientTransport(new HttpClientTransportOptions
            {
                Name = mount.Name,
                Endpoint = new Uri(mount.Url!),
            }),
            _ => throw new NotSupportedException(
                $"MCP mount '{mount.Name}': {mount.Transport} transport needs a {(mount.Transport == McpTransport.Stdio ? "command" : "url")}."),
        };

        var client = await McpClient.CreateAsync(transport, cancellationToken: ct).ConfigureAwait(false);
        _clients.Add(client);

        var tools = await client.ListToolsAsync(cancellationToken: ct).ConfigureAwait(false);
        var result = new List<AITool>(tools.Count);
        foreach (var tool in tools)
        {
            await audit.RecordAsync(new AuditEntry($"mcp:{mount.Name}", tool.Name, RiskTier.Medium, "mounted"), ct).ConfigureAwait(false);
            AIFunction fn = mount.GateMutatingTools
                ? new GatedAIFunction(tool, () => GateAsync(mount, tool.Name))
                : tool;
            // Bound + log every call, so a wedged tool can't hang the turn forever and the log names the culprit.
            result.Add(new TimedAIFunction(fn, ToolTimeout, mount.Name, log));
        }
        return result;
    }

    private async Task<bool> GateAsync(McpMount mount, string toolName)
    {
        var request = new ApprovalRequest($"mcp:{mount.Name}:{toolName}", RiskTier.Medium,
            $"Run the MCP tool '{toolName}' on '{mount.Name}'", $"{mount.Name} → {toolName}");
        var decision = await approval.RequestApprovalAsync(request).ConfigureAwait(false);
        await audit.RecordAsync(new AuditEntry($"mcp:{mount.Name}", toolName, RiskTier.Medium, decision.Approved ? "approved" : "denied"));
        return decision.Approved;
    }

    public async ValueTask DisposeAsync()
    {
        foreach (var client in _clients)
            await client.DisposeAsync().ConfigureAwait(false);
        _clients.Clear();
    }
}

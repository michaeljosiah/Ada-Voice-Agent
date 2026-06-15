using Microsoft.Extensions.AI;

namespace Ada.Core;

/// <summary>
/// Wraps any <see cref="AIFunction"/> (e.g. a tool from a mounted MCP server) so an async gate runs
/// before it — used to put external, write-capable MCP tools behind Ada's approval flow. Name,
/// description, and schema pass straight through, so the model sees the original tool.
/// </summary>
public sealed class GatedAIFunction(AIFunction inner, Func<Task<bool>> gate) : DelegatingAIFunction(inner)
{
    protected override async ValueTask<object?> InvokeCoreAsync(AIFunctionArguments arguments, CancellationToken cancellationToken)
    {
        if (!await gate().ConfigureAwait(false))
            return "Denied by the user.";

        return await base.InvokeCoreAsync(arguments, cancellationToken).ConfigureAwait(false);
    }
}

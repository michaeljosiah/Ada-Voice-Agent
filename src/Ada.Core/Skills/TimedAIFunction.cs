using System.Diagnostics;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;

namespace Ada.Core;

/// <summary>
/// Wraps an <see cref="AIFunction"/> (e.g. a mounted MCP tool) so one call can never hang the whole agent
/// turn. It logs each invocation's start and finish, and cancels the call after <paramref name="timeout"/>,
/// returning an error string the model can recover from instead of leaving the turn stuck on "thinking"
/// forever. This is exactly the failure mode a slow/wedged sandbox tool produces. Name, description and
/// schema pass straight through, so the model still sees the original tool.
/// </summary>
public sealed class TimedAIFunction(AIFunction inner, TimeSpan timeout, string source, ILogger? log = null)
    : DelegatingAIFunction(inner)
{
    protected override async ValueTask<object?> InvokeCoreAsync(AIFunctionArguments arguments, CancellationToken cancellationToken)
    {
        var sw = Stopwatch.StartNew();
        log?.LogDebug("[tool:{Source}] '{Tool}' invoked", source, Name);

        // Race the call against a timer rather than relying on cooperative cancellation: a wedged MCP/sandbox
        // call may never observe its CancellationToken, so an `await call` with CancelAfter would hang anyway
        // (this is exactly why a 60s *cooperative* cap still froze the turn). With Task.WhenAny we ABANDON the
        // stuck call when the timer wins and return an error, so the turn always proceeds.
        using var callCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var call = base.InvokeCoreAsync(arguments, callCts.Token).AsTask();
        using var delayCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var winner = await Task.WhenAny(call, Task.Delay(timeout, delayCts.Token)).ConfigureAwait(false);

        if (winner == call)
        {
            delayCts.Cancel(); // stop the timer
            var result = await call.ConfigureAwait(false); // surface the real result / exception
            log?.LogDebug("[tool:{Source}] '{Tool}' returned in {Ms} ms", source, Name, sw.ElapsedMilliseconds);
            return result;
        }

        // The timer won. If the *outer* token was cancelled, propagate that; otherwise it's our timeout —
        // abandon the (possibly un-cancellable) call so the turn doesn't hang on "thinking" forever.
        cancellationToken.ThrowIfCancellationRequested();
        callCts.Cancel(); // best-effort
        _ = call.ContinueWith(static t => _ = t.Exception, TaskScheduler.Default); // observe to avoid unobserved-task tear-down
        log?.LogWarning("[tool:{Source}] '{Tool}' did not respond within {Secs}s — abandoned so the turn doesn't hang on \"thinking\".",
            source, Name, (int)timeout.TotalSeconds);
        return $"The tool '{Name}' timed out after {(int)timeout.TotalSeconds}s. Tell the user the tool isn't responding; do not retry it.";
    }
}

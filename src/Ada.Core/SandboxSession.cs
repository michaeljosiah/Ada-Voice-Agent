using Microsoft.Extensions.AI;

namespace Ada.Core;

/// <summary>
/// The live state of Ada's work environment, shared between the background sandbox bring-up and the
/// lazily-built agent. When the AIO sandbox is up, <see cref="Tools"/> are its MCP tools (shell, files,
/// browser, code) and the host fs/shell tools they replace are suppressed; otherwise it stays inactive
/// and the agent uses host tools — AIO-first, host-fallback.
/// <para>
/// Bring-up runs in the background so app startup never blocks (mirroring the managed Ollama). The
/// agent is built on the first request and briefly waits on <see cref="WaitUntilReady"/> so it always
/// composes against the settled environment. Outside the server (CLI one-shots, tests) no bring-up
/// runs, so the session starts ready and nothing waits.
/// </para>
/// </summary>
public sealed class SandboxSession
{
    // The host tools the sandbox supersedes when active, so the agent uses the sandbox's isolated
    // shell/filesystem rather than the host's. Memory/schedule tools are kept either way.
    private static readonly HashSet<string> HostFileAndShellTools =
        new(StringComparer.Ordinal) { "read_file", "write_file", "list_directory", "delete_file", "run_command" };

    private volatile TaskCompletionSource _ready = CompletedSource();

    /// <summary>True when the sandbox is up and its tools are mounted.</summary>
    public bool Active { get; private set; }

    /// <summary>"sandbox" when active, otherwise "host".</summary>
    public string Mode => Active ? "sandbox" : "host";

    /// <summary>The sandbox API base when active.</summary>
    public string? Endpoint { get; private set; }

    /// <summary>The host folder backing the workspace (bind-mounted into the sandbox when active).</summary>
    public string Workspace { get; } = AdaPaths.WorkspaceDir;

    /// <summary>The sandbox's mounted MCP tools when active; empty otherwise.</summary>
    public IReadOnlyList<AITool> Tools { get; private set; } = [];

    /// <summary>Marks a bring-up as in progress; the agent's first build waits for it to settle.</summary>
    public void BeginInitialization() =>
        _ready = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

    /// <summary>The sandbox is up: record its endpoint and mounted tools, and release any waiters.</summary>
    public void Activate(string endpoint, IReadOnlyList<AITool> tools)
    {
        Endpoint = endpoint;
        Tools = tools;
        Active = true;
        _ready.TrySetResult();
    }

    /// <summary>The sandbox is unavailable or disabled: stay on host tools, and release any waiters.</summary>
    public void Deactivate()
    {
        Active = false;
        Endpoint = null;
        Tools = [];
        _ready.TrySetResult();
    }

    /// <summary>Blocks up to <paramref name="timeout"/> until the environment has settled; returns
    /// immediately when no bring-up is in progress.</summary>
    public void WaitUntilReady(TimeSpan timeout)
    {
        try { _ready.Task.Wait(timeout); }
        catch { /* best-effort — fall through to whatever state we have */ }
    }

    /// <summary>Direct tools safe to expose alongside the active sandbox. Host fs/shell tools are removed
    /// when the sandbox is active so filesystem and command work stays inside the sandbox boundary.</summary>
    public IReadOnlyList<AITool> DirectToolsFor(IReadOnlyList<AITool> baseTools)
    {
        if (!Active) return baseTools;
        return baseTools.Where(t => !HostFileAndShellTools.Contains(t.Name)).ToList();
    }

    /// <summary>Legacy broad tool surface: direct host-safe tools plus all sandbox tools.</summary>
    public IReadOnlyList<AITool> ApplyTo(IReadOnlyList<AITool> baseTools)
    {
        if (!Active) return baseTools;
        return DirectToolsFor(baseTools).Concat(Tools).ToList();
    }

    private static TaskCompletionSource CompletedSource()
    {
        var tcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        tcs.SetResult();
        return tcs;
    }
}

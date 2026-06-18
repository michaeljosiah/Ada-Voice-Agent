using System.Diagnostics;
using System.Text.Json;
using Microsoft.Agents.AI;

namespace Ada.Core;

/// <summary>
/// Runs a file-based skill's bundled script — the MAF <c>run_skill_script</c> tool — inside the AIO
/// sandbox via <c>docker exec</c>, so a skill's (untrusted) code executes in the container, never on the
/// host. Ada mounts the skills folder read-only into the container, so the script she discovered on disk
/// is already present inside at the same relative path. When the sandbox isn't running the runner
/// declines and tells the agent to start it: by design (the founder's choice) skill scripts are
/// sandbox-only, and the gate is the sandbox boundary, so runs inside it need no per-script approval.
/// </summary>
public sealed class AioSkillScriptRunner(SandboxSession session, AioSandboxOptions? options = null)
{
    private readonly AioSandboxOptions _options = options ?? new AioSandboxOptions();

    /// <summary>Matches the <see cref="AgentFileSkillScriptRunner"/> delegate shape.</summary>
    public async Task<object?> RunAsync(AgentFileSkill skill, AgentFileSkillScript script, JsonElement? arguments, IServiceProvider? serviceProvider, CancellationToken cancellationToken)
    {
        if (!session.Active)
            return "Ada's sandbox isn't running, and skill scripts only run inside it. Ask the user to turn it on in " +
                   "Settings → Workspace & sandbox (or start it), then try again.";

        // The skills folder is mounted read-only at ContainerSkillsDir, so map the host script path to its
        // path inside the container (forward slashes). Refuse anything resolving outside the skills root.
        var rel = Path.GetRelativePath(AdaPaths.SkillsDir, script.FullPath).Replace('\\', '/');
        if (rel.StartsWith("..", StringComparison.Ordinal) || Path.IsPathRooted(rel))
            return $"Refusing to run '{script.FullPath}': it is outside the skills folder.";
        var containerPath = $"{_options.ContainerSkillsDir}/{rel}";

        // Pick the interpreter from the script's extension; the sandbox carries both Python and Node.
        var interpreter = Path.GetExtension(script.FullPath).Equals(".js", StringComparison.OrdinalIgnoreCase) ? "node" : "python3";
        var args = new List<string> { "exec", _options.ContainerName, interpreter, containerPath };
        args.AddRange(ToPositionalArgs(arguments));

        var (ok, stdout, stderr, code) = await DockerAsync(args, cancellationToken).ConfigureAwait(false);
        if (ok) return stdout.Length == 0 ? "(the script produced no output)" : stdout;
        return $"The script failed (exit {code}).{(stderr.Length > 0 ? "\n" + stderr : string.Empty)}";
    }

    // File-based scripts receive arguments as a JSON array of strings, each becoming a positional CLI arg.
    private static IEnumerable<string> ToPositionalArgs(JsonElement? arguments)
    {
        if (arguments is { ValueKind: JsonValueKind.Array } arr)
            foreach (var el in arr.EnumerateArray())
                yield return el.ValueKind == JsonValueKind.String ? el.GetString() ?? string.Empty : el.GetRawText();
    }

    private static async Task<(bool ok, string stdout, string stderr, int code)> DockerAsync(IReadOnlyList<string> args, CancellationToken ct)
    {
        var psi = new ProcessStartInfo("docker")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        foreach (var a in args) psi.ArgumentList.Add(a);

        try
        {
            using var p = Process.Start(psi);
            if (p is null) return (false, string.Empty, "could not start docker", -1);

            var stdoutTask = p.StandardOutput.ReadToEndAsync(ct);
            var stderrTask = p.StandardError.ReadToEndAsync(ct);
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(TimeSpan.FromMinutes(2)); // a runaway script never blocks the turn forever
            try { await p.WaitForExitAsync(cts.Token).ConfigureAwait(false); }
            catch (OperationCanceledException) { try { p.Kill(entireProcessTree: true); } catch { /* gone */ } return (false, string.Empty, "the script timed out after 2 minutes", -1); }

            return (p.ExitCode == 0, (await stdoutTask).Trim(), (await stderrTask).Trim(), p.ExitCode);
        }
        catch (Exception ex) { return (false, string.Empty, ex.Message, -1); }
    }
}

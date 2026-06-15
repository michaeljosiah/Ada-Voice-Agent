using System.ComponentModel;
using System.Diagnostics;
using Ada.Core;

namespace Ada.Tools;

/// <summary>
/// Ada's shell. Always gated and audited. Unlike a file write, a command isn't bounded by the
/// allowed roots, so it's a heavier tier; true confinement of arbitrary commands is the container
/// zone (M5). Here the guard is approval + audit.
/// </summary>
public sealed class ShellTools(ToolContext ctx)
{
    [Description("Run a shell command and return its combined output. Requires approval.")]
    public async Task<string> RunCommand([Description("The command line to run.")] string command)
    {
        if (string.IsNullOrWhiteSpace(command)) return "Empty command.";

        var request = new ApprovalRequest("run_command", RiskTier.Medium, "Run a shell command", command);
        if (!await ctx.GateAsync(request))
        {
            await ctx.Audit.RecordAsync(new AuditEntry("run_command", command, RiskTier.Medium, "denied"));
            return "Denied by the user.";
        }

        var psi = new ProcessStartInfo
        {
            FileName = OperatingSystem.IsWindows() ? "cmd.exe" : "/bin/sh",
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        psi.ArgumentList.Add(OperatingSystem.IsWindows() ? "/c" : "-c");
        psi.ArgumentList.Add(command);

        using var proc = Process.Start(psi)!;
        var stdout = await proc.StandardOutput.ReadToEndAsync();
        var stderr = await proc.StandardError.ReadToEndAsync();
        await proc.WaitForExitAsync();
        await ctx.Audit.RecordAsync(new AuditEntry("run_command", command, RiskTier.Medium, $"exit:{proc.ExitCode}"));

        var output = stdout;
        if (!string.IsNullOrWhiteSpace(stderr)) output += $"\n[stderr]\n{stderr}";
        return string.IsNullOrWhiteSpace(output) ? $"(no output, exit {proc.ExitCode})" : output.TrimEnd();
    }
}

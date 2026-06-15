using System.Diagnostics;

namespace Ada.Core;

/// <summary>
/// Registers a Windows Task Scheduler task that runs the job runner headless, so jobs fire when Ada
/// is closed (spec §12.5). One periodic task drives all cron jobs; the runner's catch-up handles any
/// misses. User-level task — no admin required.
/// </summary>
public static class WindowsJobRunnerTask
{
    public const string DefaultTaskName = "AdaJobRunner";

    public static bool Install(string command, int everyMinutes = 10, string taskName = DefaultTaskName)
        => Schtasks($"/create /tn \"{taskName}\" /tr \"{command}\" /sc minute /mo {everyMinutes} /f");

    public static bool Uninstall(string taskName = DefaultTaskName)
        => Schtasks($"/delete /tn \"{taskName}\" /f");

    public static bool Exists(string taskName = DefaultTaskName)
        => Schtasks($"/query /tn \"{taskName}\"");

    private static bool Schtasks(string arguments)
    {
        try
        {
            var psi = new ProcessStartInfo("schtasks", arguments)
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            using var p = Process.Start(psi);
            if (p is null) return false;
            if (!p.WaitForExit(8000)) { try { p.Kill(); } catch { /* ignore */ } return false; }
            return p.ExitCode == 0;
        }
        catch
        {
            return false;
        }
    }
}

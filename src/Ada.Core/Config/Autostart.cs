using Microsoft.Win32;

namespace Ada.Core;

/// <summary>
/// Manages Ada's autostart via the per-user Run key (spec §15) — no admin required. Off Windows the
/// methods are no-ops returning false.
/// </summary>
public static class Autostart
{
    private const string RunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";

    public static bool Enable(string exePath, string valueName = "Ada")
    {
        if (!OperatingSystem.IsWindows()) return false;
        using var key = Registry.CurrentUser.OpenSubKey(RunKeyPath, writable: true) ?? Registry.CurrentUser.CreateSubKey(RunKeyPath);
        // --tray: an autostart launch stays hidden in the tray; a manual double-click (no args) opens the window.
        key.SetValue(valueName, $"\"{exePath}\" --tray");
        return true;
    }

    public static bool Disable(string valueName = "Ada")
    {
        if (!OperatingSystem.IsWindows()) return false;
        using var key = Registry.CurrentUser.OpenSubKey(RunKeyPath, writable: true);
        if (key?.GetValue(valueName) is null) return false;
        key.DeleteValue(valueName, throwOnMissingValue: false);
        return true;
    }

    public static bool IsEnabled(string valueName = "Ada")
    {
        if (!OperatingSystem.IsWindows()) return false;
        using var key = Registry.CurrentUser.OpenSubKey(RunKeyPath);
        return key?.GetValue(valueName) is not null;
    }
}

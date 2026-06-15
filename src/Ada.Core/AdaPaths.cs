namespace Ada.Core;

/// <summary>
/// Where Ada keeps her local state — persona, memory, audit log, config — under the user's
/// roaming app data (<c>%APPDATA%\Ada</c>). Everything here is plain, inspectable files.
/// </summary>
public static class AdaPaths
{
    /// <summary>The root data directory. Created on demand by <see cref="EnsureDataDir"/>.</summary>
    public static string DataDir { get; } =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Ada");

    public static string EnsureDataDir()
    {
        Directory.CreateDirectory(DataDir);
        return DataDir;
    }

    /// <summary>The editable persona / system-prompt file.</summary>
    public static string PersonaFile => Path.Combine(DataDir, "ADA.md");
}

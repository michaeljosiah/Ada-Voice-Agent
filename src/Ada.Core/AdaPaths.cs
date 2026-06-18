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

    /// <summary>
    /// The agent's working folder — the one place Ada's file/shell tools may write (besides Downloads).
    /// On the host it is <c>%APPDATA%\Ada\workspace</c>; when the AIO sandbox is up this same folder is
    /// bind-mounted into the container at <c>/home/gem/workspace</c>, so the agent operates on one
    /// consistent workspace whether it runs in the sandbox or falls back to the host.
    /// </summary>
    public static string WorkspaceDir { get; } = Path.Combine(DataDir, "workspace");

    /// <summary>Ensures the workspace folder exists and returns it.</summary>
    public static string EnsureWorkspaceDir()
    {
        Directory.CreateDirectory(WorkspaceDir);
        return WorkspaceDir;
    }

    /// <summary>The editable persona / system-prompt file.</summary>
    public static string PersonaFile => Path.Combine(DataDir, "ADA.md");
}

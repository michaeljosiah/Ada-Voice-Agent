namespace Ada.Core;

public sealed class ScopeViolationException(string message) : Exception(message);

/// <summary>
/// Decides what Ada may touch. Scope is enforced <em>independently of approval</em>: a user can
/// approve an action and it will STILL be blocked if it falls outside the allowed roots or hits a
/// denied/secret path. This is the blast-radius guarantee (spec §8.3) — approval never widens scope.
/// </summary>
public interface IScopePolicy
{
    IReadOnlyList<string> AllowedRoots { get; }
    bool IsReadAllowed(string path);
    bool IsWriteAllowed(string path);

    /// <summary>Resolves a path (collapsing <c>..</c>) and asserts it is readable; throws otherwise.</summary>
    string ResolveForRead(string path);

    /// <summary>Resolves a path and asserts it is writable (inside an allowed root, not denied); throws otherwise.</summary>
    string ResolveForWrite(string path);
}

public sealed class ScopePolicy : IScopePolicy
{
    private static readonly StringComparison PathCmp =
        OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;

    private readonly string[] _allowed;
    private readonly string[] _writeDenied;
    private readonly string[] _secret;

    public ScopePolicy(IEnumerable<string> allowedRoots, IEnumerable<string> writeDeniedRoots, IEnumerable<string> secretRoots)
    {
        _allowed = allowedRoots.Select(NormalizeRoot).ToArray();
        _writeDenied = writeDeniedRoots.Select(NormalizeRoot).ToArray();
        _secret = secretRoots.Select(NormalizeRoot).ToArray();
    }

    public IReadOnlyList<string> AllowedRoots => _allowed;

    /// <summary>The shippable default: write only inside the Ada workspace and Downloads; never write
    /// to Windows or Program Files; never read or write Ada's own secrets vault. The workspace is
    /// <c>%APPDATA%\Ada\workspace</c> — the same folder bind-mounted into the AIO sandbox — created
    /// here so the allowed root always exists.</summary>
    public static ScopePolicy Default()
    {
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var allowed = new[]
        {
            AdaPaths.EnsureWorkspaceDir(),
            Path.Combine(home, "Downloads"),
        };
        var writeDenied = new[]
        {
            Environment.GetFolderPath(Environment.SpecialFolder.Windows),
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
        };
        var secret = new[] { Path.Combine(AdaPaths.DataDir, "secrets") };
        return new ScopePolicy(allowed, writeDenied, secret);
    }

    public string ResolveForRead(string path)
    {
        var full = Resolve(path);
        if (!IsReadAllowedResolved(full)) throw new ScopeViolationException($"Reading '{full}' is outside Ada's scope.");
        return full;
    }

    public string ResolveForWrite(string path)
    {
        var full = Resolve(path);
        if (!IsWriteAllowedResolved(full)) throw new ScopeViolationException($"Writing '{full}' is outside Ada's allowed roots.");
        return full;
    }

    public bool IsReadAllowed(string path) => IsReadAllowedResolved(Resolve(path));
    public bool IsWriteAllowed(string path) => IsWriteAllowedResolved(Resolve(path));

    // Reads are ungated anywhere except the secrets vault.
    private bool IsReadAllowedResolved(string full) => !IsWithinAny(full, _secret);

    // Writes must be inside an allowed root and never in a denied or secret path — even if approved.
    private bool IsWriteAllowedResolved(string full)
        => IsWithinAny(full, _allowed) && !IsWithinAny(full, _writeDenied) && !IsWithinAny(full, _secret);

    private static bool IsWithinAny(string path, string[] roots) => roots.Any(r => IsWithin(path, r));

    private static bool IsWithin(string path, string root)
    {
        if (path.Equals(root, PathCmp)) return true;
        var prefix = root + Path.DirectorySeparatorChar;
        return path.StartsWith(prefix, PathCmp);
    }

    // GetFullPath collapses "." and ".." — defeating path-traversal escapes like allowed\..\Windows.
    private static string Resolve(string path)
    {
        if (string.IsNullOrWhiteSpace(path)) throw new ScopeViolationException("Empty path.");
        return Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
    }

    private static string NormalizeRoot(string root) => Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));
}

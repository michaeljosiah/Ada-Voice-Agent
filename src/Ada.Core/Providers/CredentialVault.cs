using System.Security.Cryptography;
using System.Text;

namespace Ada.Core;

/// <summary>The OS-backed secret store. API keys live here, never in a plaintext file (spec §14.2).</summary>
public interface ICredentialVault
{
    void Set(string key, string secret);
    string? Get(string key);
    void Delete(string key);
    bool Has(string key);
}

/// <summary>
/// Stores secrets encrypted with Windows DPAPI (per-user scope) as opaque blobs under
/// <c>%APPDATA%\Ada\secrets</c>. The bytes on disk are unreadable to any other user or machine.
/// Ada is Windows-only; off Windows DPAPI throws.
/// </summary>
public sealed class DpapiCredentialVault : ICredentialVault
{
    private readonly string _dir;

    public DpapiCredentialVault(string? dir = null)
    {
        _dir = dir ?? Path.Combine(AdaPaths.DataDir, "secrets");
        Directory.CreateDirectory(_dir);
    }

    public void Set(string key, string secret)
    {
        var blob = ProtectedData.Protect(Encoding.UTF8.GetBytes(secret), optionalEntropy: null, DataProtectionScope.CurrentUser);
        File.WriteAllBytes(PathFor(key), blob);
    }

    public string? Get(string key)
    {
        var path = PathFor(key);
        if (!File.Exists(path)) return null;
        var clear = ProtectedData.Unprotect(File.ReadAllBytes(path), optionalEntropy: null, DataProtectionScope.CurrentUser);
        return Encoding.UTF8.GetString(clear);
    }

    public void Delete(string key) { var p = PathFor(key); if (File.Exists(p)) File.Delete(p); }
    public bool Has(string key) => File.Exists(PathFor(key));

    private string PathFor(string key) => Path.Combine(_dir, Sanitize(key) + ".dpapi");
    private static string Sanitize(string key) => string.Concat(key.Select(c => char.IsLetterOrDigit(c) ? c : '_'));
}

/// <summary>In-memory vault for tests and non-Windows dev.</summary>
public sealed class InMemoryCredentialVault : ICredentialVault
{
    private readonly Dictionary<string, string> _store = new();
    public void Set(string key, string secret) => _store[key] = secret;
    public string? Get(string key) => _store.GetValueOrDefault(key);
    public void Delete(string key) => _store.Remove(key);
    public bool Has(string key) => _store.ContainsKey(key);
}

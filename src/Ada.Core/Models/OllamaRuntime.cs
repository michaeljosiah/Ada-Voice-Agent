using System.Diagnostics;
using System.IO.Compression;
using System.Net.Http.Json;
using System.Text.Json;

namespace Ada.Core;

/// <summary>Where Ada's managed Ollama lives and what to run.</summary>
public sealed record OllamaOptions(
    string Endpoint = "http://127.0.0.1:11434",
    string? RuntimeDir = null,
    string DownloadUrl = "https://github.com/ollama/ollama/releases/latest/download/ollama-windows-amd64.zip",
    string DefaultModel = "gemma3:4b");

/// <summary>
/// Manages Ollama as a loopback subprocess so a local model is "automatically available" with zero
/// user setup. It detects an already-running/installed Ollama and uses that; otherwise it downloads
/// the standalone binaries into <c>%APPDATA%\Ada\ollama</c> and runs <c>ollama serve</c> on loopback,
/// with models under Ada's data dir. Ada's <c>openai-compatible</c> provider then talks to it at
/// <c>:11434/v1</c>. An externally-owned Ollama is never killed.
/// </summary>
public sealed class OllamaRuntime : IAsyncDisposable
{
    private readonly Process? _process; // null when we're using the user's own Ollama

    private OllamaRuntime(string endpoint, Process? process) { Endpoint = endpoint; _process = process; }

    /// <summary>The native endpoint (e.g. http://127.0.0.1:11434). The OpenAI surface is this + "/v1".</summary>
    public string Endpoint { get; }

    /// <summary>True if Ada launched this Ollama (and will stop it); false if it found an existing one.</summary>
    public bool Managed => _process is not null;

    public static string DefaultRuntimeDir => Path.Combine(AdaPaths.DataDir, "ollama");

    public static async Task<bool> IsReachableAsync(string endpoint, CancellationToken ct = default)
    {
        try
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(3) };
            return (await http.GetAsync($"{endpoint}/api/version", ct)).IsSuccessStatusCode;
        }
        catch { return false; }
    }

    public static string? FindExecutable(string? runtimeDir)
    {
        var exeName = OperatingSystem.IsWindows() ? "ollama.exe" : "ollama";
        var managed = Path.Combine(runtimeDir ?? DefaultRuntimeDir, exeName);
        if (File.Exists(managed)) return managed;

        foreach (var p in (Environment.GetEnvironmentVariable("PATH") ?? string.Empty).Split(Path.PathSeparator))
        {
            try { var cand = Path.Combine(p, exeName); if (File.Exists(cand)) return cand; }
            catch { /* malformed PATH entry */ }
        }
        return null;
    }

    /// <summary>
    /// Brings Ollama up. Returns the running runtime, or null when it isn't installed and
    /// <paramref name="allowDownload"/> is false (so app startup never triggers a silent big download —
    /// the setup wizard does that with progress).
    /// </summary>
    public static async Task<OllamaRuntime?> StartAsync(OllamaOptions options, bool allowDownload, IProgress<string>? progress = null, CancellationToken ct = default)
    {
        if (await IsReachableAsync(options.Endpoint, ct))
            return new OllamaRuntime(options.Endpoint, null); // already running (the user's, or a prior managed one)

        var exe = FindExecutable(options.RuntimeDir);
        if (exe is null)
        {
            if (!allowDownload) return null;
            exe = await DownloadAsync(options, progress, ct);
        }

        progress?.Report("Starting Ollama…");
        var process = StartServe(exe, options);

        for (var i = 0; i < 40 && !await IsReachableAsync(options.Endpoint, ct); i++)
            await Task.Delay(500, ct);

        return new OllamaRuntime(options.Endpoint, process);
    }

    /// <summary>Pulls a model (streaming status), e.g. <c>gemma4:e4b</c>.</summary>
    public async Task PullAsync(string model, IProgress<string>? progress = null, CancellationToken ct = default)
    {
        using var http = new HttpClient { Timeout = Timeout.InfiniteTimeSpan };
        using var req = new HttpRequestMessage(HttpMethod.Post, $"{Endpoint}/api/pull") { Content = JsonContent.Create(new { model, stream = true }) };
        using var resp = await http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct);
        resp.EnsureSuccessStatusCode();

        await using var stream = await resp.Content.ReadAsStreamAsync(ct);
        using var reader = new StreamReader(stream);
        string? line;
        while ((line = await reader.ReadLineAsync(ct)) is not null)
        {
            try
            {
                using var doc = JsonDocument.Parse(line);
                if (doc.RootElement.TryGetProperty("status", out var s)) progress?.Report(s.GetString() ?? string.Empty);
            }
            catch (JsonException) { /* keep-alive line */ }
        }
    }

    private static Process StartServe(string exe, OllamaOptions options)
    {
        var modelsDir = Path.Combine(options.RuntimeDir ?? DefaultRuntimeDir, "models");
        Directory.CreateDirectory(modelsDir);
        var host = new Uri(options.Endpoint);

        var psi = new ProcessStartInfo(exe, "serve")
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        psi.Environment["OLLAMA_HOST"] = $"{host.Host}:{host.Port}";
        psi.Environment["OLLAMA_MODELS"] = modelsDir;
        return Process.Start(psi)!;
    }

    private static async Task<string> DownloadAsync(OllamaOptions options, IProgress<string>? progress, CancellationToken ct)
    {
        var dir = options.RuntimeDir ?? DefaultRuntimeDir;
        Directory.CreateDirectory(dir);
        var zip = Path.Combine(dir, "ollama.zip");

        progress?.Report("Downloading Ollama…");
        using (var http = new HttpClient { Timeout = Timeout.InfiniteTimeSpan })
        await using (var src = await http.GetStreamAsync(options.DownloadUrl, ct))
        await using (var fs = File.Create(zip))
        {
            await src.CopyToAsync(fs, ct);
        }

        progress?.Report("Extracting Ollama…");
        ZipFile.ExtractToDirectory(zip, dir, overwriteFiles: true);
        File.Delete(zip);

        return FindExecutable(dir) ?? throw new InvalidOperationException("ollama executable not found after extraction.");
    }

    public async ValueTask DisposeAsync()
    {
        if (_process is { HasExited: false })
        {
            try { _process.Kill(entireProcessTree: true); await _process.WaitForExitAsync(); }
            catch { /* already gone */ }
        }
        _process?.Dispose();
    }
}

using System.Collections.Concurrent;
using System.Diagnostics;

namespace Ada.Core;

/// <summary>
/// One of the Docker images Ada's autonomy ladder relies on. A <see cref="Core"/> image is the work
/// environment itself (the AIO sandbox — browser, shell, files, code); the rest are <c>run_code</c>
/// language runtimes. A null <see cref="Digest"/> means we pull by tag; set a sha256 digest to pin an
/// exact image for reproducibility/supply-chain safety.
/// </summary>
public sealed record ManagedImage(
    string Key, string Repository, string Tag, string Title, string Purpose, bool Core, string? Digest = null)
{
    /// <summary>What we hand to <c>docker pull</c>/<c>inspect</c>: pinned by digest when set, else by tag.</summary>
    public string Reference => Digest is { Length: > 0 } d ? $"{Repository}@{d}" : $"{Repository}:{Tag}";
}

/// <summary>The state of one managed image on this machine. <see cref="SizeText"/> is Docker's own
/// human-readable size (e.g. "1.24GB"), empty when the image isn't present.</summary>
public sealed record ImageState(
    string Key, string Title, string Purpose, string Reference, bool Core, bool Present, string SizeText);

/// <summary>Docker availability plus the whole image catalog as it stands on this machine right now.</summary>
public sealed record ProvisionStatus(bool DockerAvailable, IReadOnlyList<ImageState> Images);

/// <summary>
/// Ada's single source of truth for the Docker images her sandbox and <c>run_code</c> capabilities need,
/// and the one place that downloads them. <b>The user never types <c>docker pull</c>.</b> The setup flow
/// pulls with progress, a background prefetch tops up missing runtimes once the sandbox is set up, and
/// Settings shows what's on disk. Images are pulled from their registries on the user's machine — never
/// bundled in the installer: together they're several GB and aren't ours to redistribute, and a pinned
/// snapshot would go stale. Everything here is best-effort and never throws: no Docker ⇒ empty/false.
/// </summary>
public sealed class ImageProvisioner
{
    /// <summary>
    /// The images Ada manages. AIO is the work environment (the only "core" one); the others back
    /// <c>run_code</c>. Keep these references in step with <see cref="AioSandboxOptions.Image"/> and the
    /// container code sandbox. Pin <see cref="ManagedImage.Digest"/> once we publish a tested set.
    /// </summary>
    public static IReadOnlyList<ManagedImage> Catalog { get; } =
    [
        new("aio", "ghcr.io/agent-infra/sandbox", "latest", "Sandbox — browser, shell, files, code",
            "Ada's isolated work environment, with her own browser and terminal.", Core: true),
        new("dotnet", "mcr.microsoft.com/dotnet/sdk", "10.0", "C# runtime",
            "Runs C# snippets for run_code (.NET 10 file-based apps).", Core: false),
        new("python", "python", "3-alpine", "Python runtime",
            "Runs Python snippets for run_code.", Core: false),
        new("node", "node", "alpine", "JavaScript runtime",
            "Runs JavaScript snippets for run_code.", Core: false),
    ];

    /// <summary>Resolve a catalog entry by its short key ("aio"/"dotnet"/…) or its full image reference.</summary>
    public static ManagedImage? Find(string keyOrReference) =>
        Catalog.FirstOrDefault(i =>
            string.Equals(i.Key, keyOrReference, StringComparison.OrdinalIgnoreCase) ||
            string.Equals(i.Reference, keyOrReference, StringComparison.Ordinal));

    // De-dupe concurrent background pulls of the same reference (e.g. run_code fired twice in a turn).
    private readonly ConcurrentDictionary<string, Task> _inFlight = new(StringComparer.Ordinal);

    /// <summary>True if a Docker daemon is reachable (required to pull or run anything).</summary>
    public Task<bool> DockerAvailableAsync(CancellationToken ct = default) =>
        AioSandboxRuntime.DockerAvailableAsync(ct);

    /// <summary>True if the image is already on this machine.</summary>
    public async Task<bool> ImageExistsAsync(string reference, CancellationToken ct = default) =>
        (await RunDocker(["image", "inspect", reference], TimeSpan.FromSeconds(15), ct)).ExitCode == 0;

    /// <summary>The on-disk size Docker reports for the image (e.g. "1.24GB"), or "" when absent. Uses
    /// <c>docker image ls</c> so the figure matches what the user sees in Docker Desktop — <c>inspect
    /// .Size</c> can differ markedly under the containerd image store (it omits content-store blobs).</summary>
    public async Task<string> ImageSizeTextAsync(string reference, CancellationToken ct = default)
    {
        var r = await RunDocker(["image", "ls", reference, "--format", "{{.Size}}"], TimeSpan.FromSeconds(15), ct);
        return r.ExitCode == 0 ? r.Stdout.Trim().Split('\n')[0].Trim() : string.Empty;
    }

    /// <summary>The whole catalog's state on this machine — drives Settings → Workspace &amp; sandbox.</summary>
    public async Task<ProvisionStatus> StatusAsync(CancellationToken ct = default)
    {
        if (!await DockerAvailableAsync(ct))
            return new ProvisionStatus(false,
                Catalog.Select(i => new ImageState(i.Key, i.Title, i.Purpose, i.Reference, i.Core, false, string.Empty)).ToList());

        var states = new List<ImageState>(Catalog.Count);
        foreach (var i in Catalog)
        {
            var present = await ImageExistsAsync(i.Reference, ct);
            var sizeText = present ? await ImageSizeTextAsync(i.Reference, ct) : string.Empty;
            states.Add(new ImageState(i.Key, i.Title, i.Purpose, i.Reference, i.Core, present, sizeText));
        }
        return new ProvisionStatus(true, states);
    }

    /// <summary>Pulls one image, reporting coarse layer progress. True on success (or already present).</summary>
    public async Task<bool> PullAsync(ManagedImage image, IProgress<string>? progress = null, CancellationToken ct = default)
    {
        if (await ImageExistsAsync(image.Reference, ct)) return true;
        progress?.Report($"Preparing to download {image.Title}…");
        return await PullReferenceAsync(image.Reference, image.Title, progress, ct);
    }

    /// <summary>
    /// Pulls every catalog image not yet present, core-first. By default the big AIO image is skipped —
    /// it's only ever pulled by the explicit setup flow, so a background top-up never starts a multi-GB
    /// download the user didn't ask for. Best-effort: keeps going if one fails. Returns how many are
    /// present afterwards (of those it considered).
    /// </summary>
    public async Task<int> PrefetchMissingAsync(IProgress<string>? progress = null, bool includeCore = false, CancellationToken ct = default)
    {
        var present = 0;
        foreach (var i in Catalog)
        {
            if (i.Core && !includeCore) continue;
            if (await ImageExistsAsync(i.Reference, ct) || await PullAsync(i, progress, ct)) present++;
        }
        return present;
    }

    /// <summary>
    /// Fire-and-forget pull for the lazy <c>run_code</c> fallback: a tool call that finds its runtime
    /// missing kicks this off and returns promptly. De-duplicated by reference so a repeated call doesn't
    /// pull twice; swallows everything (background best-effort).
    /// </summary>
    public void PullInBackground(string reference) =>
        _ = _inFlight.GetOrAdd(reference, r => Task.Run(async () =>
        {
            try { await PullReferenceAsync(r, Find(r)?.Title ?? r, null, CancellationToken.None); }
            catch { /* best effort */ }
            finally { _inFlight.TryRemove(r, out _); }
        }));

    /// <summary>True while a background pull of this reference is in flight.</summary>
    public bool IsPulling(string reference) => _inFlight.ContainsKey(reference);

    // Runs `docker pull` and turns its line-per-layer output into friendly progress. Without a TTY, Docker
    // prints one line per layer state change ("<12-hex id>: Pull complete") and a "Status:"/"Digest:"
    // footer — no byte counts, so we report completed-vs-seen layers, which is honest and informative.
    private static async Task<bool> PullReferenceAsync(string reference, string title, IProgress<string>? progress, CancellationToken ct)
    {
        try
        {
            var psi = new ProcessStartInfo("docker")
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            psi.ArgumentList.Add("pull");
            psi.ArgumentList.Add(reference);

            using var p = Process.Start(psi);
            if (p is null) return false;

            var seen = new HashSet<string>(StringComparer.Ordinal);
            var done = new HashSet<string>(StringComparer.Ordinal);
            string? line;
            while ((line = await p.StandardOutput.ReadLineAsync(ct)) is not null)
            {
                var colon = line.IndexOf(':');
                if (colon <= 0) continue;
                var id = line[..colon];
                if (!IsLayerId(id)) continue; // skip the tag header and the Status:/Digest: footer
                seen.Add(id);
                var status = line[(colon + 1)..].Trim();
                if (status.StartsWith("Pull complete", StringComparison.Ordinal) ||
                    status.StartsWith("Already exists", StringComparison.Ordinal))
                    done.Add(id);
                progress?.Report($"Downloading {title}… {done.Count}/{seen.Count} layers");
            }

            await p.WaitForExitAsync(ct);
            var ok = p.ExitCode == 0;
            progress?.Report(ok ? $"{title} ready." : $"Couldn't download {title}.");
            return ok;
        }
        catch (OperationCanceledException) { throw; }
        catch { return false; }
    }

    // Docker short layer ids are 12 lowercase-hex chars — this excludes tags ("latest", "3-alpine") and
    // footer labels ("Status", "Digest") that also precede a colon.
    private static bool IsLayerId(string s)
    {
        if (s.Length != 12) return false;
        foreach (var c in s)
            if (c is not ((>= '0' and <= '9') or (>= 'a' and <= 'f'))) return false;
        return true;
    }

    private static async Task<(int ExitCode, string Stdout)> RunDocker(IReadOnlyList<string> args, TimeSpan timeout, CancellationToken ct)
    {
        try
        {
            var psi = new ProcessStartInfo("docker")
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            foreach (var a in args) psi.ArgumentList.Add(a);

            using var p = Process.Start(psi);
            if (p is null) return (-1, string.Empty);

            var outTask = p.StandardOutput.ReadToEndAsync(ct);
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(timeout);
            try { await p.WaitForExitAsync(cts.Token); }
            catch (OperationCanceledException) { try { p.Kill(entireProcessTree: true); } catch { /* gone */ } return (-1, string.Empty); }

            return (p.ExitCode, await outTask);
        }
        catch { return (-1, string.Empty); }
    }
}

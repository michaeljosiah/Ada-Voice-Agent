namespace Ada.Core;

public sealed record DownloadProgress(string File, long BytesDone, long? BytesTotal, int FileIndex, int FileCount);

/// <summary>
/// Downloads a set of files from a public Hugging Face repo into one flat folder, streaming progress.
/// Files already present (non-empty) are skipped, so an interrupted download resumes file-by-file.
/// No auth — the catalog uses ungated repos.
/// </summary>
public sealed class HuggingFaceDownloader(HttpClient? http = null)
{
    private readonly HttpClient _http = http ?? new HttpClient { Timeout = Timeout.InfiniteTimeSpan };

    public async Task DownloadAsync(
        string repo, string subfolder, IReadOnlyList<string> files, string targetDir,
        IProgress<DownloadProgress>? progress = null, CancellationToken ct = default)
    {
        Directory.CreateDirectory(targetDir);
        var prefix = string.IsNullOrEmpty(subfolder) ? string.Empty : subfolder.TrimEnd('/') + "/";

        for (var i = 0; i < files.Count; i++)
        {
            var file = files[i];
            var dest = Path.Combine(targetDir, file);
            Directory.CreateDirectory(Path.GetDirectoryName(dest)!);

            if (File.Exists(dest) && new FileInfo(dest).Length > 0)
            {
                progress?.Report(new DownloadProgress(file, new FileInfo(dest).Length, new FileInfo(dest).Length, i + 1, files.Count));
                continue;
            }

            var url = $"https://huggingface.co/{repo}/resolve/main/{prefix}{file}";
            using var response = await _http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
            response.EnsureSuccessStatusCode();
            var total = response.Content.Headers.ContentLength;

            await using var source = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
            var tmp = dest + ".part";
            await using (var fileStream = File.Create(tmp))
            {
                var buffer = new byte[1 << 20];
                long done = 0;
                int read;
                while ((read = await source.ReadAsync(buffer, ct).ConfigureAwait(false)) > 0)
                {
                    await fileStream.WriteAsync(buffer.AsMemory(0, read), ct).ConfigureAwait(false);
                    done += read;
                    progress?.Report(new DownloadProgress(file, done, total, i + 1, files.Count));
                }
            }
            File.Move(tmp, dest, overwrite: true);
        }
    }
}

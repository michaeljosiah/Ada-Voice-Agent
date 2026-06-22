namespace Ada.Core;

/// <summary>
/// Manages downloaded local models under <c>%APPDATA%\Ada\models\&lt;id&gt;</c>. A model is "ready"
/// when its folder contains a <c>genai_config.json</c> (the marker ONNX Runtime GenAI loads). A model
/// can also be dropped in by hand (e.g. bundled in the installer) — same check.
/// </summary>
public sealed class OnnxModelStore
{
    private readonly string _root;

    public OnnxModelStore(string? root = null) => _root = root ?? Path.Combine(AdaPaths.DataDir, "models");

    public string DirFor(string id) => Path.Combine(_root, id);

    /// <summary>Delete a downloaded ONNX model's folder to free disk. Returns false if it wasn't present.</summary>
    public bool Delete(string id)
    {
        var dir = DirFor(id);
        if (!Directory.Exists(dir)) return false;
        try { Directory.Delete(dir, recursive: true); return true; }
        catch { return false; }
    }

    public bool IsReady(string id) => File.Exists(Path.Combine(DirFor(id), "genai_config.json"));

    public IReadOnlyList<string> Downloaded() =>
        Directory.Exists(_root)
            ? Directory.GetDirectories(_root).Select(d => Path.GetFileName(d)!).Where(IsReady).ToList()
            : [];

    public async Task<string> DownloadAsync(OnnxModelEntry model, IProgress<DownloadProgress>? progress = null, CancellationToken ct = default)
    {
        var dir = DirFor(model.Id);
        await new HuggingFaceDownloader().DownloadAsync(model.Repo, model.Subfolder, model.Files, dir, progress, ct).ConfigureAwait(false);
        if (!IsReady(model.Id))
            throw new InvalidOperationException($"Downloaded '{model.Id}' but no genai_config.json is present — not an ONNX Runtime GenAI model.");
        return dir;
    }
}

using System.IO.Compression;
using System.Text.RegularExpressions;

namespace Ada.Core;

/// <summary>The outcome of installing an uploaded skill archive.</summary>
public sealed record SkillInstallResult(bool Ok, string? Name = null, string? Error = null);

/// <summary>
/// Installs a file-based skill from an uploaded <c>.zip</c> / <c>.skill</c> archive into
/// <see cref="AdaPaths.SkillsDir"/>, after validating it. Extraction is the only thing that happens at
/// install time — a skill's scripts never run here; they run later, gated, inside the AIO sandbox — so
/// the risks to guard are archive-shaped: zip-slip (path traversal), zip bombs, and malformed skills.
/// </summary>
public static partial class SkillInstaller
{
    private const long MaxTotalBytes = 50L * 1024 * 1024; // 50 MB uncompressed — a skill is small
    private const int MaxEntries = 4000;

    public static SkillInstallResult InstallFromZip(Stream archiveStream)
    {
        ZipArchive archive;
        try
        {
            archive = new ZipArchive(archiveStream, ZipArchiveMode.Read, leaveOpen: true);
        }
        catch (InvalidDataException)
        {
            return new SkillInstallResult(false, Error: "That file isn't a valid .zip / .skill archive.");
        }

        using (archive)
        {
            // ---- archive-shape guards ----
            if (archive.Entries.Count > MaxEntries)
                return new SkillInstallResult(false, Error: "The archive has too many files.");

            long total = 0;
            foreach (var e in archive.Entries)
            {
                total += e.Length;
                if (total > MaxTotalBytes)
                    return new SkillInstallResult(false, Error: "The archive is too large (over 50 MB uncompressed).");
                if (IsUnsafePath(e.FullName))
                    return new SkillInstallResult(false, Error: $"The archive contains an unsafe path: '{e.FullName}'.");
            }

            // ---- find the SKILL.md (shallowest wins) and its directory prefix ----
            var skillEntry = archive.Entries
                .Where(e => string.Equals(Path.GetFileName(e.FullName.Replace('\\', '/')), "SKILL.md", StringComparison.OrdinalIgnoreCase))
                .OrderBy(e => e.FullName.Count(c => c is '/' or '\\'))
                .FirstOrDefault();
            if (skillEntry is null)
                return new SkillInstallResult(false, Error: "No SKILL.md found in the archive — a skill needs one.");

            var prefix = DirPrefix(skillEntry.FullName);

            // ---- validate the frontmatter ----
            string md;
            using (var reader = new StreamReader(skillEntry.Open()))
                md = reader.ReadToEnd();

            var name = FrontmatterField(md, "name");
            var description = FrontmatterField(md, "description");
            if (string.IsNullOrWhiteSpace(name) || string.IsNullOrWhiteSpace(description))
                return new SkillInstallResult(false, Error: "SKILL.md must declare a 'name' and a 'description' in its frontmatter.");
            if (!NamePattern().IsMatch(name))
                return new SkillInstallResult(false, Error: $"Skill name '{name}' is invalid — use lowercase letters, numbers and single hyphens (e.g. my-skill).");

            // ---- extract the skill's subtree into skills/<name> (staged, then swapped in) ----
            var target = Path.Combine(AdaPaths.EnsureSkillsDir(), name);
            var staging = target + ".incoming-" + Guid.NewGuid().ToString("n")[..8];
            try
            {
                foreach (var e in archive.Entries)
                {
                    var full = e.FullName.Replace('\\', '/');
                    if (!full.StartsWith(prefix, StringComparison.Ordinal)) continue;
                    var rel = full[prefix.Length..];
                    if (rel.Length == 0 || rel.EndsWith('/')) continue; // directory entry

                    var dest = SafeCombine(staging, rel);
                    if (dest is null)
                        return Fail(staging, $"Unsafe path in the archive: '{e.FullName}'.");

                    Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
                    e.ExtractToFile(dest, overwrite: true);
                }

                // Swap into place last, so a partial/failed extract never destroys an existing skill.
                if (Directory.Exists(target)) Directory.Delete(target, recursive: true);
                Directory.Move(staging, target);
                return new SkillInstallResult(true, Name: name);
            }
            catch (Exception ex)
            {
                return Fail(staging, $"Couldn't install the skill: {ex.Message}");
            }
        }
    }

    private static SkillInstallResult Fail(string staging, string error)
    {
        try { if (Directory.Exists(staging)) Directory.Delete(staging, recursive: true); }
        catch { /* best effort */ }
        return new SkillInstallResult(false, Error: error);
    }

    // Reject anything that could escape the target: absolute paths, drive letters, or "..".
    private static bool IsUnsafePath(string entry)
    {
        var p = entry.Replace('\\', '/');
        if (p.StartsWith('/')) return true;
        if (p.Length >= 2 && p[1] == ':') return true;          // C:\...
        if (p == ".." || p.StartsWith("../") || p.Contains("/../") || p.EndsWith("/..")) return true;
        return false;
    }

    private static string DirPrefix(string skillMdFullName)
    {
        var p = skillMdFullName.Replace('\\', '/');
        var idx = p.LastIndexOf('/');
        return idx < 0 ? string.Empty : p[..(idx + 1)]; // keeps the trailing slash
    }

    // A second-line zip-slip guard: resolve the destination and confirm it stays under the staging root.
    private static string? SafeCombine(string root, string rel)
    {
        var dest = Path.GetFullPath(Path.Combine(root, rel));
        var rootFull = Path.GetFullPath(root) + Path.DirectorySeparatorChar;
        var cmp = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        return dest.StartsWith(rootFull, cmp) ? dest : null;
    }

    private static string? FrontmatterField(string md, string key)
    {
        var fm = Regex.Match(md, @"^---\s*\r?\n(.*?)\r?\n---", RegexOptions.Singleline);
        var block = fm.Success ? fm.Groups[1].Value : md;
        var field = Regex.Match(block, $@"(?m)^{Regex.Escape(key)}:\s*(.+?)\s*$");
        return field.Success ? field.Groups[1].Value.Trim().Trim('"', '\'') : null;
    }

    [GeneratedRegex(@"^[a-z0-9]+(-[a-z0-9]+)*$")]
    private static partial Regex NamePattern();
}

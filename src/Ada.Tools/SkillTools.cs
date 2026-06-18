using System.ComponentModel;
using System.IO.Compression;
using Ada.Core;

namespace Ada.Tools;

/// <summary>
/// Lets Ada install a file-based skill she has authored in her workspace into <see cref="AdaPaths.SkillsDir"/>
/// — the one place skills are loaded from, which is deliberately <em>outside</em> her normal write scope.
/// This is the gated bridge that closes the "describe a skill → Ada builds it → it's installed" loop while
/// keeping the safety boundary: the source must live inside the workspace, the skill goes through the exact
/// same <see cref="SkillInstaller"/> validation as a Settings upload, and installing always needs approval
/// (it adds new, script-running capability). A skill's scripts never run here — only on Ada's next launch,
/// gated, inside the sandbox.
/// </summary>
public sealed class SkillTools(ToolContext ctx)
{
    [Description("Install a skill you have authored into Ada's skills folder so it becomes available. " +
                 "Pass the path to the skill folder you created under the workspace — it must contain a SKILL.md. " +
                 "Installing always asks the user to approve (it adds new capability) and takes effect on Ada's next launch. " +
                 "The scripts are not run during install.")]
    public async Task<string> InstallSkill(
        [Description("Path to the authored skill folder inside the workspace, e.g. \"my-skill\" or the full workspace path.")] string folder)
    {
        if (string.IsNullOrWhiteSpace(folder))
            return "No skill folder was provided.";

        var workspace = Path.TrimEndingDirectorySeparator(Path.GetFullPath(AdaPaths.EnsureWorkspaceDir()));

        // The agent may pass a workspace-relative path, a host path, or the container-side workspace path
        // (/home/gem/workspace/…) it used while the sandbox was active — normalise all three to the host.
        var raw = folder.Replace('\\', '/');
        var containerWs = new AioSandboxOptions().ContainerWorkspace.Replace('\\', '/'); // /home/gem/workspace
        if (raw.StartsWith(containerWs, StringComparison.OrdinalIgnoreCase))
            raw = raw[containerWs.Length..].TrimStart('/');

        string full;
        try { full = Path.GetFullPath(Path.IsPathRooted(raw) ? raw : Path.Combine(workspace, raw)); }
        catch { return $"'{folder}' isn't a valid path."; }

        // Ada may only install skills she authored in her own workspace — never an arbitrary host folder.
        var cmp = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        if (!full.Equals(workspace, cmp) && !full.StartsWith(workspace + Path.DirectorySeparatorChar, cmp))
            return "The skill folder must be inside Ada's workspace. Author it there first, then install it.";
        if (!Directory.Exists(full))
            return $"There's no folder at '{folder}'. Create the skill there first.";
        if (!File.Exists(Path.Combine(full, "SKILL.md")))
            return "That folder has no SKILL.md — a skill needs one. Add it, then install.";

        // Gate: installing grants new, script-capable behaviour outside the normal write scope, so it
        // always asks (High risk). The approval card shows the exact source folder.
        var request = new ApprovalRequest("install_skill", RiskTier.High, "Install a skill into Ada's skills folder", full);
        if (!await ctx.GateAsync(request))
        {
            await ctx.Audit.RecordAsync(new AuditEntry("install_skill", full, RiskTier.High, "denied"));
            return "Install was declined.";
        }

        // Zip the folder in memory (SKILL.md at the archive root) and run it through the SAME validation a
        // Settings upload uses — frontmatter name/description, name slug, zip-slip and size/entry caps.
        SkillInstallResult result;
        try
        {
            using var buffer = new MemoryStream();
            ZipFolder(full, buffer);
            buffer.Position = 0;
            result = SkillInstaller.InstallFromZip(buffer);
        }
        catch (Exception ex)
        {
            await ctx.Audit.RecordAsync(new AuditEntry("install_skill", full, RiskTier.High, "error"));
            return $"Couldn't install the skill: {ex.Message}";
        }

        await ctx.Audit.RecordAsync(new AuditEntry("install_skill", full, RiskTier.High,
            result.Ok ? $"installed:{result.Name}" : "rejected"));
        return result.Ok
            ? $"Installed the '{result.Name}' skill. It will be available on Ada's next launch."
            : $"The skill didn't pass validation: {result.Error}";
    }

    // Zip every file under root with paths relative to root, skipping Python bytecode caches.
    private static void ZipFolder(string root, Stream output)
    {
        using var zip = new ZipArchive(output, ZipArchiveMode.Create, leaveOpen: true);
        foreach (var file in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories))
        {
            var rel = Path.GetRelativePath(root, file).Replace('\\', '/');
            if (rel.Split('/').Contains("__pycache__")) continue;
            zip.CreateEntryFromFile(file, rel);
        }
    }
}

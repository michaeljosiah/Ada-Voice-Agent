namespace Ada.Core;

/// <summary>The autonomy ladder's backends (spec §8.8). M2 ships the default in-process Wasm zone.</summary>
public enum SandboxZone { Host, InProcWasm, LocalContainer, Remote }

public sealed record SandboxRequest(
    string Language,
    string Code,
    ulong Fuel = 200_000_000,
    long MemoryBytes = 64L * 1024 * 1024);

public sealed record SandboxResult(bool Ok, string Output, string Reason = "ok", string? Error = null)
{
    public static SandboxResult Ran(string output) => new(true, output);
    public static SandboxResult Failed(string reason, string? error = null) => new(false, string.Empty, reason, error);
}

/// <summary>
/// The one seam every "run code" path goes through (spec §8.8). The agent asks to run code and never
/// names a backend; the default is the in-process Wasm zone, where mutating work needs no per-step
/// approval because the blast radius is a disposable sandbox. The gate moves to the zone boundary.
/// </summary>
public interface ICodeSandbox
{
    SandboxZone Zone { get; }
    bool Available { get; }
    Task<SandboxResult> RunAsync(SandboxRequest request, CancellationToken ct = default);
}

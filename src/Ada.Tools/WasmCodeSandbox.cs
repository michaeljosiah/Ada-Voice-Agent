using Ada.Core;
using Wasmtime;

namespace Ada.Tools;

/// <summary>
/// Zone 1 of the autonomy ladder (spec §8.8): untrusted code in-process via Wasmtime, with three
/// guarantees — capability isolation (an empty linker, so the guest can import nothing), a fuel-based
/// runaway guard (an infinite loop traps as <c>OutOfFuel</c> instead of hanging), and a memory cap.
/// M2 lands the primitive and proves the guarantees on WebAssembly-text modules; the JS/Python
/// language runtimes (Javy, CPython-WASI) attach in M5 behind this same seam.
/// </summary>
public sealed class WasmCodeSandbox : ICodeSandbox
{
    public SandboxZone Zone => SandboxZone.InProcWasm;
    public bool Available => true;

    public Task<SandboxResult> RunAsync(SandboxRequest request, CancellationToken ct = default)
    {
        if (!string.Equals(request.Language, "wat", StringComparison.OrdinalIgnoreCase))
            return Task.FromResult(SandboxResult.Failed(
                "unsupported",
                $"Zone 1 (M2) runs WebAssembly text ('wat'). '{request.Language}' arrives with the language runtimes in M5."));

        return Task.FromResult(RunWat(request));
    }

    private static SandboxResult RunWat(SandboxRequest request)
    {
        try
        {
            // Engine takes ownership of the Config and disposes it.
            using var engine = new Engine(new Config().WithFuelConsumption(true));
            using var module = Module.FromText(engine, "sandbox", request.Code);
            using var linker = new Linker(engine);          // empty: the guest can import nothing
            using var store = new Store(engine);
            store.Fuel = request.Fuel;                       // runaway guard
            store.SetLimits(memorySize: request.MemoryBytes, // memory cap
                            tableElements: null, instances: null, tables: null, memories: null);

            var instance = linker.Instantiate(store, module);

            // Convention: a sandbox module exports "run" (-> i32) or a void "main".
            var run = instance.GetFunction<int>("run");
            if (run is not null)
                return SandboxResult.Ran(run().ToString());

            var main = instance.GetAction("main");
            if (main is not null) { main(); return SandboxResult.Ran("(ran)"); }

            return SandboxResult.Failed("no-entry", "Module exports neither 'run' (i32) nor 'main'.");
        }
        catch (TrapException ex) when (ex.Type == TrapCode.OutOfFuel)
        {
            return SandboxResult.Failed("trapped-fuel", "Execution exceeded its fuel budget (runaway guard).");
        }
        catch (WasmtimeException ex)
        {
            return SandboxResult.Failed("error", ex.Message);
        }
    }
}

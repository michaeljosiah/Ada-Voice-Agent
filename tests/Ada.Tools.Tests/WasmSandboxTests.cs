using Ada.Core;

namespace Ada.Tools.Tests;

/// <summary>Zone-1 guarantees: the sandbox runs isolated code, and a runaway traps on fuel
/// instead of hanging the process.</summary>
public sealed class WasmSandboxTests
{
    private const string ComputeWat = "(module (func (export \"run\") (result i32) i32.const 42))";
    private const string RunawayWat = "(module (func (export \"run\") (result i32) (loop (br 0)) (i32.const 0)))";

    [Fact]
    public async Task Runs_a_module_and_returns_its_result()
    {
        var result = await new WasmCodeSandbox().RunAsync(new SandboxRequest("wat", ComputeWat));

        Assert.True(result.Ok);
        Assert.Equal("42", result.Output);
    }

    [Fact]
    public async Task Infinite_loop_traps_on_fuel_instead_of_hanging()
    {
        var result = await new WasmCodeSandbox().RunAsync(new SandboxRequest("wat", RunawayWat, Fuel: 100_000));

        Assert.False(result.Ok);
        Assert.Equal("trapped-fuel", result.Reason);
    }

    [Fact]
    public async Task Unsupported_language_is_reported_not_run()
    {
        var result = await new WasmCodeSandbox().RunAsync(new SandboxRequest("python", "print('hi')"));

        Assert.False(result.Ok);
        Assert.Equal("unsupported", result.Reason);
    }
}

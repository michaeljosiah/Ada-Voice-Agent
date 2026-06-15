using Ada.Core;
using Microsoft.Extensions.AI;

namespace Ada.Tools.Tests;

public class SkillsTests
{
    [Fact]
    public void Composing_a_skill_adds_its_instruction_and_tools()
    {
        var persona = new Persona("BASE PERSONA");
        var skill = new FakeSkill("demo", "DEMO INSTRUCTION", [AIFunctionFactory.Create(() => "ok", "demo_tool")]);

        var composed = SkillComposer.Compose(persona, [], [skill]);

        Assert.Contains("BASE PERSONA", composed.Instructions);
        Assert.Contains("DEMO INSTRUCTION", composed.Instructions);
        Assert.Contains(composed.Tools, t => t.Name == "demo_tool");
    }

    [Fact]
    public void Skill_registry_persists_enable_and_disable()
    {
        var path = Path.Combine(Path.GetTempPath(), $"ada_skills_{Guid.NewGuid():n}.json");
        try
        {
            ISkill[] skills = [new FakeSkill("a", null, [], enabledByDefault: true), new FakeSkill("b", null, [], enabledByDefault: false)];
            var registry = new SkillRegistry(skills, path);

            Assert.True(registry.IsEnabled("a"));
            Assert.False(registry.IsEnabled("b"));

            registry.Enable("b");
            registry.Disable("a");

            var reloaded = new SkillRegistry(skills, path);
            Assert.True(reloaded.IsEnabled("b"));
            Assert.False(reloaded.IsEnabled("a"));
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public async Task Gated_function_blocks_when_the_gate_denies()
    {
        var inner = AIFunctionFactory.Create((string x) => $"ran:{x}", "echo");
        var gated = new GatedAIFunction(inner, () => Task.FromResult(false));

        var result = await gated.InvokeAsync(new AIFunctionArguments { ["x"] = "hi" });

        Assert.Contains("Denied", result?.ToString());
    }

    [Fact]
    public async Task Gated_function_delegates_when_the_gate_allows()
    {
        var inner = AIFunctionFactory.Create((string x) => $"ran:{x}", "echo");
        var gated = new GatedAIFunction(inner, () => Task.FromResult(true));

        var result = await gated.InvokeAsync(new AIFunctionArguments { ["x"] = "hi" });

        Assert.Contains("ran:hi", result?.ToString());
    }

    [Fact]
    public void Finance_records_skill_is_a_disabled_egress_seam_with_the_product_rules()
    {
        var skill = new FinanceRecordsSkill();

        Assert.False(skill.EnabledByDefault);
        Assert.NotNull(skill.Mcp);
        Assert.True(skill.Mcp!.IsEgress);
        Assert.Contains("never convert", skill.InstructionFragment, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Describe, never prescribe", skill.InstructionFragment, StringComparison.OrdinalIgnoreCase);
    }

    private sealed class FakeSkill(string name, string? fragment, IReadOnlyList<AITool> tools, bool enabledByDefault = true) : ISkill
    {
        public string Name => name;
        public string? InstructionFragment => fragment;
        public IReadOnlyList<AITool> Tools => tools;
        public McpMount? Mcp => null;
        public bool EnabledByDefault => enabledByDefault;
    }
}

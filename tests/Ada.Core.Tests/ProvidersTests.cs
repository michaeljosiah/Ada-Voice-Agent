using Ada.Core;
using Microsoft.Extensions.AI;

namespace Ada.Core.Tests;

public class ProvidersTests
{
    [Fact]
    public void Vault_round_trips_a_secret()
    {
        var vault = new InMemoryCredentialVault();
        vault.Set("provider:x", "sk-1");
        Assert.Equal("sk-1", vault.Get("provider:x"));
        Assert.True(vault.Has("provider:x"));
        vault.Delete("provider:x");
        Assert.Null(vault.Get("provider:x"));
    }

    [Fact]
    public void Store_upsert_persists_and_reloads()
    {
        var path = Path.Combine(Path.GetTempPath(), $"ada_prov_{Guid.NewGuid():n}.json");
        try
        {
            var store = new ProviderStore(path);
            store.Upsert(new ProviderConfig("anthropic", ProviderKind.Anthropic, "claude-sonnet-4-6", "https://api.anthropic.com/v1", AuthMethod.ApiKey, ModelRole.Escalation));
            store.Upsert(new ProviderConfig("anthropic", ProviderKind.Anthropic, "claude-opus-4-8", "https://api.anthropic.com/v1", AuthMethod.ApiKey, ModelRole.Escalation));

            var reloaded = new ProviderStore(path).Load();
            Assert.Single(reloaded);
            Assert.Equal("claude-opus-4-8", reloaded[0].ModelId);
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void Catalog_has_the_shippable_providers()
    {
        Assert.NotNull(ProviderCatalog.Find("anthropic"));
        Assert.NotNull(ProviderCatalog.Find("azure-openai"));
        Assert.NotNull(ProviderCatalog.Find("ollama"));
    }

    [Theory]
    [InlineData("what time is it?", ModelRole.Default)]
    [InlineData("refactor this function for me", ModelRole.Escalation)]
    [InlineData("research and compare these options step by step", ModelRole.Escalation)]
    public void Routes_on_task_shape(string message, ModelRole expected)
    {
        var policy = new RoutingPolicy(hasEscalation: true, stayLocal: false);
        Assert.Equal(expected, policy.Route([new ChatMessage(ChatRole.User, message)], null).Role);
    }

    [Fact]
    public void Stay_local_override_pins_to_default()
        => Assert.Equal(ModelRole.Default,
            new RoutingPolicy(hasEscalation: true, stayLocal: true).Route([new ChatMessage(ChatRole.User, "refactor this")], null).Role);

    [Fact]
    public void Without_escalation_everything_stays_local()
        => Assert.Equal(ModelRole.Default,
            new RoutingPolicy(hasEscalation: false, stayLocal: false).Route([new ChatMessage(ChatRole.User, "refactor this")], null).Role);

    [Fact]
    public async Task Hybrid_escalates_code_task_and_logs_egress()
    {
        var audit = new InMemoryAuditLog();
        var hybrid = new HybridChatClient(new StubChatClient(), new StubChatClient(),
            new RoutingPolicy(hasEscalation: true, stayLocal: false, "local", "anthropic"), audit);

        await foreach (var _ in hybrid.GetStreamingResponseAsync([new ChatMessage(ChatRole.User, "debug this stack trace")])) { }

        Assert.StartsWith("anthropic", hybrid.CurrentRoute);
        Assert.Contains(audit.Recent(), e => e.Outcome == "escalated");
    }

    [Fact]
    public async Task Hybrid_keeps_a_simple_turn_local()
    {
        var hybrid = new HybridChatClient(new StubChatClient(), new StubChatClient(), new RoutingPolicy(true, false));
        await foreach (var _ in hybrid.GetStreamingResponseAsync([new ChatMessage(ChatRole.User, "hello there")])) { }
        Assert.Equal("local", hybrid.CurrentRoute);
    }
}

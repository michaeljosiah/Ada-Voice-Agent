using System.Text;
using Ada.Core;

namespace Ada.Core.Tests;

public class AgentEngineTests
{
    [Fact]
    public async Task Streams_a_reply_and_tags_the_local_route()
    {
        var engine = new AgentEngine(new StubChatClient(), new Persona("You are Ada."), route: "local");

        var sb = new StringBuilder();
        var route = string.Empty;
        await foreach (var chunk in engine.RespondAsync(new AdaRequest("remember the milk")))
        {
            sb.Append(chunk.Text);
            route = chunk.Route;
        }

        Assert.Equal("local", route);
        Assert.Contains("remember the milk", sb.ToString());
    }

    [Fact]
    public void Default_persona_carries_describe_not_prescribe()
        => Assert.Contains("Describe, don't prescribe", Persona.Default);

    [Fact]
    public void Echo_provider_yields_no_chat_client()
        => Assert.Null(ModelClientFactory.Create(new AdaModelOptions { Provider = "echo" }));

    [Fact]
    public void Local_provider_builds_a_chat_client()
    {
        var client = ModelClientFactory.Create(new AdaModelOptions
        {
            Provider = "openai-compatible",
            Endpoint = "http://localhost:11434/v1",
            ModelId = "test-model",
        });

        Assert.NotNull(client);
    }
}

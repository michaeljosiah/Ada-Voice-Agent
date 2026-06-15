using System.Text;
using Ada.Core;

namespace Ada.Core.Tests;

public class EchoEngineTests
{
    [Fact]
    public async Task Echoes_the_message_and_marks_route_echo()
    {
        IAdaEngine engine = new EchoEngine();
        var text = new StringBuilder();
        string? route = null;
        await foreach (var chunk in engine.RespondAsync(new AdaRequest("hello world")))
        {
            text.Append(chunk.Text);
            route = chunk.Route;
        }

        Assert.Equal("echo", route);
        Assert.StartsWith("Ada (echo):", text.ToString());
        Assert.Contains("hello world", text.ToString());
    }

    [Fact]
    public async Task Emits_a_final_chunk()
    {
        IAdaEngine engine = new EchoEngine();
        var sawFinal = false;
        await foreach (var chunk in engine.RespondAsync(new AdaRequest("x")))
            sawFinal |= chunk.IsFinal;

        Assert.True(sawFinal);
    }
}

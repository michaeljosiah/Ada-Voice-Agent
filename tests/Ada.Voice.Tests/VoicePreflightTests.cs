using Ada.Core;
using Ada.Voice;
using Microsoft.Extensions.AI;

namespace Ada.Voice.Tests;

/// <summary>
/// The voice preflight is the guard against a silent "Hi → stuck on thinking": the input path can be
/// perfect, but if the agent's model never answers, the turn hangs forever. These pin the probe's verdicts.
/// </summary>
public class VoicePreflightTests
{
    /// <summary>A chat client whose response is whatever the test wires up.</summary>
    private sealed class FakeChatClient(Func<CancellationToken, Task<ChatResponse>> respond) : IChatClient
    {
        public Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
            => respond(cancellationToken);
        public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();
        public object? GetService(Type serviceType, object? serviceKey = null) => null;
        public void Dispose() { }
    }

    [Fact]
    public async Task A_responsive_model_is_ok()
    {
        var client = new FakeChatClient(_ => Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant, "ok"))));
        var check = await VoicePreflight.ProbeModelAsync(client, TimeSpan.FromSeconds(5));
        Assert.Equal(PreflightStatus.Ok, check.Status);
    }

    [Fact]
    public async Task A_hanging_model_fails_on_timeout_with_the_thinking_hint()
    {
        // This is the exact failure mode behind the reported hang: the model never returns a token.
        var client = new FakeChatClient(async ct => { await Task.Delay(Timeout.Infinite, ct); return null!; });
        var check = await VoicePreflight.ProbeModelAsync(client, TimeSpan.FromMilliseconds(250));
        Assert.Equal(PreflightStatus.Fail, check.Status);
        Assert.Contains("thinking", check.Detail, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task A_throwing_model_fails_and_surfaces_the_reason()
    {
        var client = new FakeChatClient(_ => throw new InvalidOperationException("Connection refused"));
        var check = await VoicePreflight.ProbeModelAsync(client, TimeSpan.FromSeconds(5));
        Assert.Equal(PreflightStatus.Fail, check.Status);
        Assert.Contains("Connection refused", check.Detail);
    }

    [Fact]
    public async Task The_offline_stub_warns_rather_than_failing()
    {
        var check = await VoicePreflight.ProbeModelAsync(new StubChatClient(), TimeSpan.FromSeconds(5));
        Assert.Equal(PreflightStatus.Warn, check.Status);
    }

    [Fact]
    public void Report_aggregates_ok_and_worst()
    {
        var warned = new PreflightReport([new("a", PreflightStatus.Ok, ""), new("b", PreflightStatus.Warn, "")]);
        Assert.True(warned.Ok);                                  // warnings don't fail the gate
        Assert.Equal(PreflightStatus.Warn, warned.Worst);

        var failed = new PreflightReport([new("a", PreflightStatus.Ok, ""), new("c", PreflightStatus.Fail, "")]);
        Assert.False(failed.Ok);
        Assert.Equal(PreflightStatus.Fail, failed.Worst);
    }
}

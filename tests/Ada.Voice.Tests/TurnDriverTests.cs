using System.Runtime.CompilerServices;
using Ada.Core;
using Ada.Voice;
using Voxa.Frames;
using Voxa.Processors;

namespace Ada.Voice.Tests;

/// <summary>
/// M10 driver behavior: Answer chunks are sanitized and spoken, Delegate chunks become
/// <see cref="BackgroundTaskRequestFrame"/>s, background results re-enter as relevance-gated
/// ChatOnly requests, and the thinker driver wraps its engine run in the background approval scope.
/// </summary>
public class TurnDriverTests
{
    private sealed class ScriptedEngine(params AdaResponseChunk[] chunks) : IAdaEngine
    {
        public AdaRequest? LastRequest;

        public async IAsyncEnumerable<AdaResponseChunk> RespondAsync(
            AdaRequest request, [EnumeratorCancellation] CancellationToken ct = default)
        {
            LastRequest = request;
            foreach (var chunk in chunks)
            {
                yield return chunk;
                await Task.Yield();
            }
        }
    }

    private sealed class ThrowingGateway : IFrontendToolGateway
    {
        public ValueTask<ToolCallResultFrame> AwaitToolResultAsync(string callId, CancellationToken ct)
            => throw new NotSupportedException("no frontend tools in these tests");
    }

    private sealed class NullEmitter : IFrameEmitter
    {
        public ValueTask EmitAsync(Frame frame, CancellationToken ct) => ValueTask.CompletedTask;
    }

    private static VoiceTurnContext Ctx(
        string userText,
        TurnTrigger trigger = TurnTrigger.UserUtterance,
        BackgroundTaskCompletedFrame? result = null) => new()
    {
        TurnId = "turn-1",
        UserText = userText,
        FrontendTools = new ThrowingGateway(),
        Emitter = new NullEmitter(),
        Trigger = trigger,
        BackgroundResult = result,
    };

    private static async Task<List<Frame>> DrainAsync(IAgentTurnDriver driver, VoiceTurnContext ctx)
    {
        var frames = new List<Frame>();
        await foreach (var frame in driver.RunTurnAsync(ctx, CancellationToken.None))
            frames.Add(frame);
        return frames;
    }

    [Fact]
    public async Task Answer_Chunks_Are_Sanitized_And_Spoken()
    {
        var engine = new ScriptedEngine(
            new AdaResponseChunk("Hello **there**. "),
            new AdaResponseChunk(string.Empty, IsFinal: true));
        var driver = new AdaEngineTurnDriver(engine, _ => null);

        var frames = await DrainAsync(driver, Ctx("hi"));

        var chunk = Assert.Single(frames.OfType<LlmTextChunkFrame>());
        Assert.DoesNotContain("**", chunk.Text); // VoiceTextSanitizer applied
        Assert.Equal("hi", engine.LastRequest!.Message);
        Assert.True(engine.LastRequest.AllowDelegation); // voice turns may hand off
    }

    [Fact]
    public async Task A_Delegate_Chunk_Becomes_A_Background_Task_Request()
    {
        var engine = new ScriptedEngine(
            new AdaResponseChunk(string.Empty, Kind: AdaResponseChunkKind.Delegate, Goal: "research the thing"),
            new AdaResponseChunk("On it.", Route: "local"),
            new AdaResponseChunk(string.Empty, IsFinal: true));
        var driver = new AdaEngineTurnDriver(engine, _ => null);

        var frames = await DrainAsync(driver, Ctx("look this up"));

        var request = Assert.Single(frames.OfType<BackgroundTaskRequestFrame>());
        Assert.Equal("research the thing", request.Goal);
        Assert.Equal("turn-1", request.OriginTurnId); // staleness anchor
        Assert.Contains(frames.OfType<LlmTextChunkFrame>(), f => f.Text.Contains("On it"));
    }

    [Fact]
    public async Task A_Background_Result_ReEnters_As_A_ChatOnly_Relevance_Gated_Turn()
    {
        var engine = new ScriptedEngine(
            new AdaResponseChunk("Your flight lands at six."),
            new AdaResponseChunk(string.Empty, IsFinal: true));
        var threadCalls = new List<TurnTrigger>();
        var driver = new AdaEngineTurnDriver(engine, ctx => { threadCalls.Add(ctx.Trigger); return "thread-9"; });

        var result = new BackgroundTaskCompletedFrame("task-1", "UA12 lands 18:02 local");
        var frames = await DrainAsync(driver, Ctx(string.Empty, TurnTrigger.BackgroundResult, result));

        Assert.True(engine.LastRequest!.ChatOnly);                       // never re-enters tool mode
        Assert.False(engine.LastRequest.AllowDelegation);                // and never re-delegates
        Assert.Contains("UA12 lands 18:02 local", engine.LastRequest.Message);
        Assert.Contains("respond with NOTHING", engine.LastRequest.Message); // Voxa's relevance gate wording
        Assert.Equal("thread-9", engine.LastRequest.ThreadId);           // same conversation, if one exists
        Assert.Single(frames.OfType<LlmTextChunkFrame>());
        Assert.Equal([TurnTrigger.BackgroundResult], threadCalls);       // factory sees the trigger (no create)
    }

    [Fact]
    public async Task The_Thinker_Driver_Emits_Status_Then_Raw_Text()
    {
        var engine = new ScriptedEngine(
            new AdaResponseChunk("Found: the answer is 42."),
            new AdaResponseChunk(string.Empty, IsFinal: true));
        var driver = new AdaBackgroundTurnDriver(engine);

        var frames = await DrainAsync(driver, Ctx("research the thing"));

        Assert.IsType<StatusFrame>(frames[0]); // progress line for the UI
        var text = Assert.Single(frames.OfType<LlmTextChunkFrame>());
        Assert.Equal("Found: the answer is 42.", text.Text); // no sanitizer — this is read, not spoken
        Assert.Equal("research the thing", engine.LastRequest!.Message);
        Assert.Null(engine.LastRequest.ThreadId); // the thinker never writes conversation threads
    }
}

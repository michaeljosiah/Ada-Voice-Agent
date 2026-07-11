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
            if (chunk.Kind == AdaResponseChunkKind.Answer)
                sb.Append(chunk.Text);
            route = chunk.Route;
        }

        Assert.Equal("local", route);
        Assert.Contains("remember the milk", sb.ToString());
    }

    [Fact]
    public async Task Interrupted_turn_persists_user_message_and_partial_answer()
    {
        // Voice barge-in cancels the caller's token mid-stream. The exchange must not vanish from
        // history — the user's question and the truncated partial both need to land.
        var engine = new AgentEngine(new StubChatClient(), new Persona("You are Ada."));
        using var cts = new CancellationTokenSource();

        var answers = 0;
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
        {
            await foreach (var chunk in engine.RespondAsync(new AdaRequest("tell me a long story"), cts.Token))
            {
                if (chunk.Kind == AdaResponseChunkKind.Answer && chunk.Text.Length > 0 && ++answers == 2)
                    cts.Cancel(); // the next stream move throws for the caller, like the voice loop's cancel
            }
        });

        Assert.True(answers >= 2);
        Assert.Equal(2, engine.HistoryMessageCount); // user + truncated assistant, exactly once
    }

    [Fact]
    public async Task Interrupted_turn_marks_the_partial_as_interrupted_in_the_stored_thread()
    {
        var store = new MemoryConversationStore();
        var convo = store.Create("t");
        var engine = new AgentEngine(new StubChatClient(), new Persona("You are Ada."), conversations: store);
        using var cts = new CancellationTokenSource();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
        {
            await foreach (var chunk in engine.RespondAsync(new AdaRequest("tell me a long story", convo.Id), cts.Token))
            {
                if (chunk.Kind == AdaResponseChunkKind.Answer && chunk.Text.Length > 0)
                    cts.Cancel();
            }
        });

        var saved = store.Load(convo.Id)!;
        Assert.Equal("user", saved.Messages[0].Role);
        Assert.Equal("tell me a long story", saved.Messages[0].Text);
        Assert.Equal("assistant", saved.Messages[1].Role);
        Assert.EndsWith("…(interrupted)", saved.Messages[1].Text);
    }

    [Fact]
    public async Task Completed_turn_persists_exactly_once()
    {
        // Guards the cancellation-safety path against double-persist on the normal route.
        var engine = new AgentEngine(new StubChatClient(), new Persona("You are Ada."));
        await foreach (var _ in engine.RespondAsync(new AdaRequest("remember the milk"))) { }
        Assert.Equal(2, engine.HistoryMessageCount); // user + assistant, once
    }

    [Fact]
    public async Task Interrupted_background_delivery_never_persists_its_synthetic_user_message()
    {
        // A background-result delivery (PersistUserMessage: false) interrupted mid-speech records
        // only the partial spoken reply — the synthetic "[System note…]" prompt must stay out.
        var engine = new AgentEngine(new StubChatClient(), new Persona("You are Ada."));
        using var cts = new CancellationTokenSource();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
        {
            await foreach (var chunk in engine.RespondAsync(
                new AdaRequest("[System note] a result arrived", ChatOnly: true, PersistUserMessage: false), cts.Token))
            {
                if (chunk.Kind == AdaResponseChunkKind.Answer && chunk.Text.Length > 0)
                    cts.Cancel();
            }
        });

        Assert.Equal(1, engine.HistoryMessageCount); // the partial assistant reply only
    }

    private sealed class MemoryConversationStore : IConversationStore
    {
        private readonly Dictionary<string, Conversation> _byId = [];

        public Conversation Create(string? title)
        {
            var convo = new Conversation { Id = Guid.NewGuid().ToString("N"), Title = title ?? "New chat" };
            _byId[convo.Id] = convo;
            return convo;
        }

        public Conversation? Load(string id) => _byId.TryGetValue(id, out var convo) ? convo : null;
        public void Save(Conversation conversation) => _byId[conversation.Id] = conversation;
        public IReadOnlyList<ConversationSummary> List() => [];
        public bool Delete(string id) => _byId.Remove(id);
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

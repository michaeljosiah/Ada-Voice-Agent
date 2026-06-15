using System.Runtime.CompilerServices;
using Microsoft.Extensions.AI;

namespace Ada.Core;

/// <summary>
/// An offline <see cref="IChatClient"/> that fabricates a short, deterministic reply. It lets the
/// real agent path (persona + Agent Framework + streaming + routing) be exercised with no model,
/// no keys and no network — used by the CLI self-test and unit tests so M1 is verifiable headlessly.
/// </summary>
public sealed class StubChatClient : IChatClient
{
    public Task<ChatResponse> GetResponseAsync(
        IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
        => Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant, Compose(messages))));

    public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
        IEnumerable<ChatMessage> messages, ChatOptions? options = null,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        foreach (var word in Compose(messages).Split(' '))
        {
            cancellationToken.ThrowIfCancellationRequested();
            yield return new ChatResponseUpdate(ChatRole.Assistant, word + " ");
            await Task.Delay(8, cancellationToken).ConfigureAwait(false);
        }
    }

    public object? GetService(Type serviceType, object? serviceKey = null) => null;

    public void Dispose() { }

    private static string Compose(IEnumerable<ChatMessage> messages)
    {
        var user = messages.LastOrDefault(m => m.Role == ChatRole.User)?.Text;
        return string.IsNullOrWhiteSpace(user)
            ? "(stub model) Hello — I'm Ada, running on a stand-in model."
            : $"(stub model) You said: {user}";
    }
}

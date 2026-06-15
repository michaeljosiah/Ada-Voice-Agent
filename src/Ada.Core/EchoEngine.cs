using System.Runtime.CompilerServices;

namespace Ada.Core;

/// <summary>
/// M0 stand-in brain: echoes the user's message back, streamed word by word so the whole
/// streaming path (engine → loopback server → WebView2 UI) is exercised end to end before a
/// real model is wired in at M1.
/// </summary>
public sealed class EchoEngine : IAdaEngine
{
    public async IAsyncEnumerable<AdaResponseChunk> RespondAsync(
        AdaRequest request, [EnumeratorCancellation] CancellationToken ct = default)
    {
        var reply = $"Ada (echo): {request.Message}";
        var words = reply.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        for (var i = 0; i < words.Length; i++)
        {
            ct.ThrowIfCancellationRequested();
            var piece = i == words.Length - 1 ? words[i] : words[i] + " ";
            yield return new AdaResponseChunk(piece, "echo");
            await Task.Delay(15, ct).ConfigureAwait(false);
        }
        yield return new AdaResponseChunk(string.Empty, "echo", IsFinal: true);
    }
}

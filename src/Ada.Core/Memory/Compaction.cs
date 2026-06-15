using Microsoft.Extensions.AI;

namespace Ada.Core;

/// <summary>Keeps a long conversation inside the model's window (spec §8.4) without losing the thread.</summary>
public interface ICompactionStrategy
{
    Task<List<ChatMessage>> CompactAsync(List<ChatMessage> history, CancellationToken ct = default);
}

/// <summary>Never compacts — for short sessions and tests.</summary>
public sealed class NoCompaction : ICompactionStrategy
{
    public Task<List<ChatMessage>> CompactAsync(List<ChatMessage> history, CancellationToken ct = default)
        => Task.FromResult(history);
}

/// <summary>
/// When the running history grows past a character budget, summarise everything except the most
/// recent turns into a single system message and keep the rest verbatim. The summariser is pluggable:
/// a model (the summarizer-role provider) in production, or a deterministic fallback for tests.
/// </summary>
public sealed class LengthCompactionStrategy(
    int maxChars = 12_000,
    int keepRecent = 6,
    Func<IReadOnlyList<ChatMessage>, CancellationToken, Task<string>>? summarize = null) : ICompactionStrategy
{
    private readonly Func<IReadOnlyList<ChatMessage>, CancellationToken, Task<string>> _summarize =
        summarize ?? ((msgs, _) => Task.FromResult($"[{msgs.Count} earlier messages condensed]"));

    public async Task<List<ChatMessage>> CompactAsync(List<ChatMessage> history, CancellationToken ct = default)
    {
        var total = history.Sum(m => m.Text?.Length ?? 0);
        if (total <= maxChars || history.Count <= keepRecent)
            return history;

        var recent = history.TakeLast(keepRecent).ToList();
        var older = history.Take(history.Count - keepRecent).ToList();
        var summary = await _summarize(older, ct).ConfigureAwait(false);

        var compacted = new List<ChatMessage> { new(ChatRole.System, "Summary of earlier conversation: " + summary) };
        compacted.AddRange(recent);
        return compacted;
    }
}

/// <summary>A model-backed summariser for <see cref="LengthCompactionStrategy"/>.</summary>
public static class ModelSummarizer
{
    public static Func<IReadOnlyList<ChatMessage>, CancellationToken, Task<string>> For(IChatClient client) =>
        async (messages, ct) =>
        {
            var transcript = string.Join("\n", messages.Select(m => $"{m.Role}: {m.Text}"));
            var prompt = new List<ChatMessage>
            {
                new(ChatRole.System, "Summarise the following conversation concisely, preserving names, decisions, and open threads."),
                new(ChatRole.User, transcript),
            };
            var response = await client.GetResponseAsync(prompt, cancellationToken: ct).ConfigureAwait(false);
            return response.Text;
        };
}

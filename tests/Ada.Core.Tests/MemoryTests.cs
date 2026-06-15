using Ada.Core;
using Microsoft.Extensions.AI;

namespace Ada.Core.Tests;

public sealed class MemoryTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("ada_mem_test_").FullName;

    public void Dispose() { try { Directory.Delete(_dir, true); } catch { /* best effort */ } }

    [Fact]
    public void Remember_writes_a_readable_file_and_indexes_it()
    {
        using var store = new FileMemoryStore(_dir);
        var entry = store.Remember("Accountant Tunde", "accountant; year-end 31 March", MemoryType.Reference, "Tunde is my accountant. Year-end 31 March.");

        Assert.Equal("accountant-tunde", entry.Name);
        Assert.True(File.Exists(Path.Combine(_dir, entry.Name + ".md")));
        Assert.Contains(entry.Name, store.IndexMarkdown());
    }

    [Fact]
    public void Recall_finds_a_memory_by_content()
    {
        using var store = new FileMemoryStore(_dir);
        store.Remember("flat", "the Surulere flat costs", MemoryType.Project, "The flat in Surulere had three commitments this quarter.");

        Assert.Contains(store.Recall("how much did the flat cost"), h => h.Name == "flat");
    }

    [Fact]
    public void A_new_session_recalls_persisted_memory()
    {
        using (var first = new FileMemoryStore(_dir))
            first.Remember("pref", "user prefers brevity", MemoryType.User, "The user prefers brief answers.");

        using var second = new FileMemoryStore(_dir);
        Assert.Contains(second.Recall("brief"), h => h.Name == "pref");
    }

    [Fact]
    public void Forget_removes_the_file_and_the_index_line()
    {
        using var store = new FileMemoryStore(_dir);
        var entry = store.Remember("temp", "temporary", MemoryType.Reference, "delete me");

        Assert.True(store.Forget("temp"));
        Assert.False(File.Exists(Path.Combine(_dir, entry.Name + ".md")));
        Assert.DoesNotContain("temp", store.IndexMarkdown());
        Assert.False(store.Forget("temp"));
    }

    [Fact]
    public async Task Context_provider_includes_the_user_model_and_recalled_memory()
    {
        using var store = new FileMemoryStore(_dir);
        store.Remember("accountant-tunde", "accountant year end", MemoryType.Reference, "Tunde is the accountant; year-end 31 March.");
        var user = new UserModel(Path.Combine(_dir, "USER.md"));
        user.Write("The user is Michael, a Windows power user.");

        var context = await new MemoryContextProvider(store, user).BuildAsync("when is the accountant's year end?");

        Assert.NotNull(context);
        Assert.Contains("Michael", context);
        Assert.Contains("accountant-tunde", context);
    }

    [Fact]
    public async Task Compaction_bounds_a_long_session_and_keeps_a_summary_plus_recent_turns()
    {
        var history = new List<ChatMessage>();
        for (var i = 0; i < 40; i++)
        {
            history.Add(new ChatMessage(ChatRole.User, new string('x', 400)));
            history.Add(new ChatMessage(ChatRole.Assistant, new string('y', 400)));
        }

        var compacted = await new LengthCompactionStrategy(maxChars: 4000, keepRecent: 6).CompactAsync(history);

        Assert.Equal(80, history.Count);
        Assert.True(compacted.Count <= 7);
        Assert.Equal(ChatRole.System, compacted[0].Role);
    }

    [Fact]
    public async Task Compaction_leaves_a_short_session_untouched()
    {
        var history = new List<ChatMessage> { new(ChatRole.User, "hi"), new(ChatRole.Assistant, "hello") };
        Assert.Equal(2, (await new LengthCompactionStrategy().CompactAsync(history)).Count);
    }
}

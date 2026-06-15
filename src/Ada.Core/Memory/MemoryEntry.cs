namespace Ada.Core;

/// <summary>The kind of memory (spec §9.1). Mirrors Ada's own auto-memory taxonomy.</summary>
public enum MemoryType { User, Feedback, Project, Reference }

/// <summary>One durable memory: a readable markdown file with frontmatter you can open and edit.</summary>
public sealed record MemoryEntry(string Name, string Description, MemoryType Type, string Body)
{
    public string ToMarkdown() =>
        $"""
        ---
        name: {Name}
        description: {Description}
        type: {Type.ToString().ToLowerInvariant()}
        ---

        {Body}
        """;
}

/// <summary>A recall result — which memory matched and a snippet of where.</summary>
public sealed record MemoryRecallHit(string Name, string Description, string Snippet);

namespace Ada.Core;

/// <summary>A known provider the wizard/CLI can offer with sensible defaults (spec §6.1).</summary>
public sealed record CatalogEntry(
    string Id, string Label, ProviderKind Kind, string? DefaultEndpoint, string DefaultModel, AuthMethod Auth);

/// <summary>The built-in provider catalog — what <c>ada auth</c> and the setup wizard offer out of the box.</summary>
public static class ProviderCatalog
{
    public static IReadOnlyList<CatalogEntry> BuiltIns { get; } =
    [
        new("anthropic", "Anthropic (Claude)", ProviderKind.Anthropic, "https://api.anthropic.com/v1", "claude-sonnet-4-6", AuthMethod.ApiKey),
        new("openai", "OpenAI", ProviderKind.OpenAiCompatible, "https://api.openai.com/v1", "gpt-4o", AuthMethod.ApiKey),
        new("azure-openai", "Azure OpenAI", ProviderKind.AzureOpenAI, null, "gpt-4o", AuthMethod.AzureCredential),
        new("ollama", "Ollama (local)", ProviderKind.OpenAiCompatible, "http://localhost:11434/v1", "qwen2.5:7b-instruct", AuthMethod.None),
        new("foundry-local", "Foundry Local", ProviderKind.FoundryLocal, "http://localhost:5273/v1", "phi-4", AuthMethod.None),
    ];

    public static CatalogEntry? Find(string id) =>
        BuiltIns.FirstOrDefault(e => string.Equals(e.Id, id, StringComparison.OrdinalIgnoreCase));
}

namespace Ada.Core;

/// <summary>The kinds of model backend Ada can talk to (spec §6.1). Each maps to an IChatClient.</summary>
public enum ProviderKind { OpenAiCompatible, Anthropic, AzureOpenAI, FoundryLocal }

/// <summary>How Ada authenticates to a provider. M3 ships the shippable methods; OAuth/CLI-reuse are
/// later research (spec §6.2).</summary>
public enum AuthMethod { None, ApiKey, AzureCredential }

/// <summary>The role a provider plays in the hybrid router (spec §6.7).</summary>
public enum ModelRole { Default, Escalation, Summarizer }

/// <summary>
/// A configured provider. Secrets are never stored here — only a reference; the key itself lives in
/// the OS vault under <c>provider:{Id}</c>.
/// </summary>
public sealed record ProviderConfig(
    string Id,
    ProviderKind Kind,
    string ModelId,
    string? Endpoint = null,
    AuthMethod Auth = AuthMethod.None,
    ModelRole Role = ModelRole.Escalation)
{
    public string VaultKey => $"provider:{Id}";
}

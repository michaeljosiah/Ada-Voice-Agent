namespace Ada.Core;

/// <summary>
/// Which brain Ada uses. M1 ships two providers: <c>echo</c> (the offline stand-in) and an
/// <c>openai-compatible</c> local endpoint (Foundry Local, Ollama, LM Studio — all expose the
/// OpenAI shape). The hybrid cloud router arrives in M3; this stays the local-default seam.
/// </summary>
public sealed class AdaModelOptions
{
    /// <summary>"echo" | "openai-compatible" | "foundry-local".</summary>
    public string Provider { get; set; } = "echo";

    /// <summary>Base URL of an OpenAI-compatible endpoint, e.g. <c>http://localhost:11434/v1</c>.</summary>
    public string? Endpoint { get; set; }

    /// <summary>The model id to request, e.g. <c>qwen2.5:7b-instruct</c>.</summary>
    public string? ModelId { get; set; }

    /// <summary>Optional API key. Local runtimes usually need none.</summary>
    public string? ApiKey { get; set; }

    /// <summary>A sensible default for a local OpenAI-compatible runtime (Ollama).</summary>
    public const string DefaultLocalEndpoint = "http://localhost:11434/v1";

    public bool IsLocalModel => Provider is "openai-compatible" or "foundry-local";

    /// <summary>Reads configuration from <c>ADA_*</c> environment variables.</summary>
    public static AdaModelOptions FromEnvironment()
    {
        var o = new AdaModelOptions();
        o.Provider = Environment.GetEnvironmentVariable("ADA_PROVIDER") ?? o.Provider;
        o.Endpoint = Environment.GetEnvironmentVariable("ADA_ENDPOINT") ?? o.Endpoint;
        o.ModelId = Environment.GetEnvironmentVariable("ADA_MODEL") ?? o.ModelId;
        o.ApiKey = Environment.GetEnvironmentVariable("ADA_API_KEY") ?? o.ApiKey;
        return o;
    }
}

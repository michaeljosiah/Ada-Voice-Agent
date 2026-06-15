using System.ClientModel;
using Microsoft.Extensions.AI;
using OpenAI;

namespace Ada.Core;

/// <summary>
/// Builds the <see cref="IChatClient"/> for the configured model provider. M1 supports
/// OpenAI-compatible local endpoints (Foundry Local, Ollama, LM Studio). Returns <c>null</c> for
/// the <c>echo</c> provider, which signals the offline stand-in brain.
/// </summary>
public static class ModelClientFactory
{
    public static IChatClient? Create(AdaModelOptions options)
    {
        if (!options.IsLocalModel)
            return null;

        var endpoint = string.IsNullOrWhiteSpace(options.Endpoint)
            ? AdaModelOptions.DefaultLocalEndpoint
            : options.Endpoint;

        var modelId = options.ModelId
            ?? throw new InvalidOperationException(
                "A local model provider requires a model id (ADA_MODEL or AdaModelOptions.ModelId).");

        // Local runtimes usually need no key, but the OpenAI client requires a credential.
        var key = string.IsNullOrEmpty(options.ApiKey) ? "local-no-key" : options.ApiKey;

        var client = new OpenAIClient(
            new ApiKeyCredential(key),
            new OpenAIClientOptions { Endpoint = new Uri(endpoint) });

        return client.GetChatClient(modelId).AsIChatClient();
    }
}

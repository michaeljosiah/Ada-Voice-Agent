using System.ClientModel;
using Azure.AI.OpenAI;
using Azure.Identity;
using Microsoft.Extensions.AI;
using OpenAI;

namespace Ada.Core;

/// <summary>
/// Builds an <see cref="IChatClient"/> from a <see cref="ProviderConfig"/>, pulling any API key from
/// the vault at call time (never from a file). OpenAI-compatible covers OpenAI, Ollama, LM Studio,
/// Foundry Local, custom URLs, and Anthropic's OpenAI-compatibility endpoint; Azure uses an API key
/// or an Azure credential.
/// </summary>
public static class ProviderClientFactory
{
    public static IChatClient Create(ProviderConfig provider, ICredentialVault vault) => provider.Kind switch
    {
        ProviderKind.AzureOpenAI => CreateAzure(provider, vault),
        _ => CreateOpenAiCompatible(provider, vault),
    };

    private static IChatClient CreateOpenAiCompatible(ProviderConfig p, ICredentialVault vault)
    {
        var endpoint = p.Endpoint ?? throw new InvalidOperationException($"Provider '{p.Id}' needs an endpoint.");
        var key = p.Auth == AuthMethod.ApiKey
            ? vault.Get(p.VaultKey) ?? throw new InvalidOperationException($"No API key in the vault for provider '{p.Id}'.")
            : "local-no-key";

        var client = new OpenAIClient(new ApiKeyCredential(key), new OpenAIClientOptions { Endpoint = new Uri(endpoint) });
        return client.GetChatClient(p.ModelId).AsIChatClient();
    }

    private static IChatClient CreateAzure(ProviderConfig p, ICredentialVault vault)
    {
        var endpoint = p.Endpoint ?? throw new InvalidOperationException($"Azure provider '{p.Id}' needs an endpoint.");
        var client = p.Auth == AuthMethod.ApiKey
            ? new AzureOpenAIClient(new Uri(endpoint),
                new ApiKeyCredential(vault.Get(p.VaultKey) ?? throw new InvalidOperationException($"No API key in the vault for provider '{p.Id}'.")))
            : new AzureOpenAIClient(new Uri(endpoint), new DefaultAzureCredential());

        return client.GetChatClient(p.ModelId).AsIChatClient();
    }
}

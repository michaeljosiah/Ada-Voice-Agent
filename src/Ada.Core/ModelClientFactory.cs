using System.ClientModel;
using System.Text;
using Microsoft.Extensions.AI;
using Microsoft.ML.OnnxRuntimeGenAI;
using OpenAI;

namespace Ada.Core;

/// <summary>
/// Builds the <see cref="IChatClient"/> for the configured model provider. The preferred local path is
/// <c>onnx</c> — an in-process ONNX Runtime GenAI model (no separate server). <c>openai-compatible</c>
/// and <c>foundry-local</c> remain for a local OpenAI-style endpoint (Ollama, LM Studio, Foundry).
/// Returns <c>null</c> for the <c>echo</c> provider, which signals the offline stand-in brain.
/// </summary>
public static class ModelClientFactory
{
    public static IChatClient? Create(AdaModelOptions options)
    {
        if (string.Equals(options.Provider, "onnx", StringComparison.OrdinalIgnoreCase))
            return CreateOnnx(options);

        if (!options.IsLocalModel)
            return null;

        return CreateOpenAiCompatible(options);
    }

    private static IChatClient CreateOnnx(AdaModelOptions options)
    {
        var store = new OnnxModelStore();
        var id = string.IsNullOrWhiteSpace(options.ModelId) ? OnnxModelCatalog.DefaultModelId : options.ModelId;
        if (!store.IsReady(id))
            throw new InvalidOperationException($"Local ONNX model '{id}' isn't downloaded yet. Run:  ada model pull {id}");

        var family = OnnxModelCatalog.Find(id)?.Family ?? "gemma";
        var clientOptions = new OnnxRuntimeGenAIChatClientOptions
        {
            StopSequences = family == "phi" ? ["<|end|>", "<|user|>"] : ["<end_of_turn>"],
            PromptFormatter = family == "phi" ? PhiPrompt : GemmaPrompt,
        };
        return new OnnxRuntimeGenAIChatClient(store.DirFor(id), clientOptions);
    }

    private static IChatClient CreateOpenAiCompatible(AdaModelOptions options)
    {
        var endpoint = string.IsNullOrWhiteSpace(options.Endpoint) ? AdaModelOptions.DefaultLocalEndpoint : options.Endpoint;
        var modelId = options.ModelId
            ?? throw new InvalidOperationException("A local OpenAI-compatible provider requires a model id (ADA_MODEL).");
        var key = string.IsNullOrEmpty(options.ApiKey) ? "local-no-key" : options.ApiKey;

        var client = new OpenAIClient(new ApiKeyCredential(key), new OpenAIClientOptions { Endpoint = new Uri(endpoint) });
        return client.GetChatClient(modelId).AsIChatClient();
    }

    // Gemma's chat format: <start_of_turn>user … <end_of_turn> … <start_of_turn>model
    private static string GemmaPrompt(IEnumerable<ChatMessage> messages, ChatOptions options)
    {
        var sb = new StringBuilder();
        foreach (var m in messages)
        {
            var role = m.Role == ChatRole.Assistant ? "model" : "user";
            sb.Append("<start_of_turn>").Append(role).Append('\n').Append(m.Text).Append("<end_of_turn>\n");
        }
        return sb.Append("<start_of_turn>model\n").ToString();
    }

    // Phi's chat format: <|user|> … <|end|> … <|assistant|>
    private static string PhiPrompt(IEnumerable<ChatMessage> messages, ChatOptions options)
    {
        var sb = new StringBuilder();
        foreach (var m in messages)
        {
            var tag = m.Role == ChatRole.System ? "system" : m.Role == ChatRole.Assistant ? "assistant" : "user";
            sb.Append("<|").Append(tag).Append("|>\n").Append(m.Text).Append("<|end|>\n");
        }
        return sb.Append("<|assistant|>\n").ToString();
    }
}

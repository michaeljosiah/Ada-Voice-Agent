namespace Ada.Core;

/// <summary>
/// A downloadable local model in ONNX Runtime GenAI format (a folder with <c>genai_config.json</c>).
/// <see cref="Files"/> are downloaded from <see cref="Repo"/>/<see cref="Subfolder"/> on Hugging Face
/// into one flat model folder.
/// </summary>
public sealed record OnnxModelEntry(
    string Id, string Label, string Repo, string Subfolder, IReadOnlyList<string> Files, int ApproxMb, string License, string Family);

/// <summary>
/// The built-in catalog of in-process local models (spec §6 / §20). ONNX Runtime GenAI is Ada's
/// preferred local provider — it runs in-process (CPU or DirectML GPU) with no separate server,
/// unlike Foundry Local / Ollama, which remain bring-your-own escape hatches.
/// </summary>
public static class OnnxModelCatalog
{
    public const string DefaultModelId = "gemma-3-1b";

    public static IReadOnlyList<OnnxModelEntry> Models { get; } =
    [
        new("gemma-3-1b", "Gemma 3 1B · int4 · ~0.9 GB (recommended)",
            "smartvest-llc/gemma-3-1b-it-genai", "",
            ["genai_config.json", "model.onnx", "model.onnx.data", "tokenizer.json", "tokenizer.model",
             "tokenizer_config.json", "special_tokens_map.json", "added_tokens.json", "chat_template.jinja"],
            945, "Gemma Terms of Use", "gemma"),

        new("gemma-3-4b", "Gemma 3 4B · int4 · ~6 GB (official ONNX Runtime build)",
            "onnxruntime/Gemma-3-ONNX", "gemma-3-4b-it/cpu_and_mobile/cpu-int4-rtn-block-32-acc-level-4",
            ["genai_config.json", "gemma-3-text.onnx", "gemma-3-text.onnx.data", "gemma-3-embedding.onnx",
             "gemma-3-embedding.onnx.data", "gemma-3-vision.onnx", "gemma-3-vision.onnx.data",
             "tokenizer.json", "tokenizer_config.json", "processor_config.json", "special_tokens_map.json", "chat_template.jinja"],
            6060, "Gemma Terms of Use", "gemma"),

        // Experimental: the only genai_config.json-format Gemma 4 (by a Microsoft ONNX maintainer).
        // Needs ONNX Runtime GenAI >= 0.14; multimodal-only, quant is bloated/mislabeled (~7.9 GB).
        new("gemma-4-e2b", "Gemma 4 E2B · EXPERIMENTAL · ~7.9 GB (multimodal, unpolished build)",
            "justinchuby/gemma-4-e2b-it-onnx", "NF4/default",
            ["genai_config.json", "decoder/model.onnx", "decoder/model.onnx.data",
             "embedding/model.onnx", "embedding/model.onnx.data",
             "audio_encoder/model.onnx", "audio_encoder/model.onnx.data",
             "vision_encoder/model.onnx", "vision_encoder/model.onnx.data",
             "tokenizer.json", "tokenizer_config.json", "chat_template.jinja",
             "image_processor.json", "audio_feature_extraction.json"],
            7924, "Gemma Terms of Use", "gemma"),

        new("phi-4-mini", "Phi-4-mini · int4 · ~4.9 GB (MIT)",
            "microsoft/Phi-4-mini-instruct-onnx", "cpu_and_mobile/cpu-int4-rtn-block-32-acc-level-4",
            ["genai_config.json", "config.json", "model.onnx", "model.onnx.data", "tokenizer.json",
             "tokenizer_config.json", "vocab.json", "merges.txt", "added_tokens.json", "special_tokens_map.json"],
            4930, "MIT", "phi"),
    ];

    public static OnnxModelEntry? Find(string id) =>
        Models.FirstOrDefault(m => string.Equals(m.Id, id, StringComparison.OrdinalIgnoreCase));
}

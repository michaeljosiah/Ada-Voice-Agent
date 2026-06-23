using System.Text;
using Ada.Core;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;

namespace Ada.Server;

/// <summary>
/// Writes a one-time snapshot of the effective configuration, the <em>actually-resolved</em> model, the
/// downloaded models, and the sandbox/voice setup to the central log at startup — so a bug report's log is
/// self-contained: "what was loaded and how was it set up" is answered without a repro, right above any
/// errors that follow. Written via <see cref="FileLoggerProvider.Append"/> so it lands in the file
/// regardless of <c>ADA_LOG</c> level (the snapshot is always wanted, even when verbose logging is off).
/// </summary>
internal static class StartupDiagnostics
{
    public static void Write(IServiceProvider services)
    {
        try
        {
            var cfg = new ConfigStore().Load();
            var options = services.GetService<AdaModelOptions>() ?? AdaModelOptions.FromEnvironment();
            var registry = services.GetService<ProviderRegistry>();

            // Resolve the loaded model the same way the engine does: a configured Default-role provider wins,
            // otherwise it's the local runtime captured in AdaModelOptions at startup.
            var def = registry?.ForRole(ModelRole.Default);
            var esc = registry?.ForRole(ModelRole.Escalation);
            var loadedRuntime = def is not null ? def.Kind.ToString()
                : options.Provider switch
                {
                    "onnx" => "onnx",
                    "openai-compatible" when options.Endpoint?.Contains(":11434", StringComparison.Ordinal) == true => "ollama",
                    _ => options.Provider,
                };
            var loadedModel = def?.ModelId ?? options.ModelId ?? "(none)";

            var ollama = OllamaRuntime.InstalledModels();
            var onnx = new OnnxModelStore().Downloaded();

            var sb = new StringBuilder();
            sb.AppendLine($"{DateTime.UtcNow:HH:mm:ss.fff} INF Ada.Startup — ===== Ada startup diagnostics =====");
            sb.AppendLine($"    app          : Ada {typeof(StartupDiagnostics).Assembly.GetName().Version}  on  {Environment.OSVersion.VersionString}");
            sb.AppendLine($"    data dir     : {AdaPaths.DataDir}");
            sb.AppendLine($"    profile      : {cfg.Profile}   setupComplete={cfg.SetupComplete}");
            sb.AppendLine($"    LOADED model : {loadedRuntime} · {loadedModel}   (provider={options.Provider}, endpoint={options.Endpoint ?? "(n/a)"})");
            sb.AppendLine($"    escalation   : {(esc is null ? "(none)" : $"{esc.Id} · {esc.ModelId}")}");
            sb.AppendLine($"    local config : runtime={cfg.LocalRuntime ?? "(unset)"}, ollamaModel={cfg.OllamaModel ?? "(unset)"}, localModelId={cfg.LocalModelId ?? "(unset)"}");
            sb.AppendLine($"    sandbox      : enabled={cfg.SandboxEnabled}, prefetchImages={cfg.PrefetchImages}");
            sb.AppendLine($"    voice        : stt={cfg.SttModel} ({cfg.SttLanguage}), tts={cfg.TtsProvider} · {cfg.TtsVoice}");
            sb.AppendLine($"    ollama models: {(ollama.Count == 0 ? "(none)" : string.Join(", ", ollama))}");
            sb.Append($"    onnx models  : {(onnx.Count == 0 ? "(none)" : string.Join(", ", onnx))}");

            FileLoggerProvider.Append(AdaPaths.LogFilePath(), sb.ToString());
        }
        catch { /* diagnostics are best-effort — never block or break startup */ }
    }
}

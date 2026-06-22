using System.Diagnostics;
using Ada.Core;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Voxa.Speech;   // VoxaModelCache, VoxaModelCacheOptions

namespace Ada.Voice;

/// <summary>Outcome of a single readiness check. <see cref="Warn"/> = works but degraded; <see cref="Fail"/> = will break a turn.</summary>
public enum PreflightStatus { Ok, Warn, Fail }

/// <summary>One line of the voice readiness report.</summary>
public sealed record PreflightCheck(string Name, PreflightStatus Status, string Detail);

/// <summary>The full voice readiness report.</summary>
public sealed record PreflightReport(IReadOnlyList<PreflightCheck> Checks)
{
    /// <summary>True when nothing is in a hard-fail state (warnings are tolerated).</summary>
    public bool Ok => !Checks.Any(c => c.Status == PreflightStatus.Fail);

    /// <summary>The most severe status across all checks.</summary>
    public PreflightStatus Worst => Checks.Count == 0 ? PreflightStatus.Ok : Checks.Max(c => c.Status);
}

/// <summary>
/// A configuration / readiness check for the voice plane — the "does everything line up before I
/// speak?" gate. It exists because a misconfigured voice setup otherwise fails <em>silently</em>: the
/// UI shows the input working (transcript appears) and then hangs on "thinking" forever when the agent's
/// model never answers. This turns that into an explicit diagnosis: speech models cached (STT, TTS, and
/// the Silero VAD that Voxa downloads on first use), and — the decisive one — the agent's model actually
/// responds within the voice response cap. Surfaced by <c>ada doctor</c> and <c>GET /api/voice/preflight</c>.
/// </summary>
public static class VoicePreflight
{
    /// <summary>The bounded wait for the model probe — kept under the LowLatency <c>MaxResponseDuration</c> (30 s).</summary>
    public static readonly TimeSpan DefaultProbeTimeout = TimeSpan.FromSeconds(12);

    /// <summary>
    /// Resolve the exact <see cref="IChatClient"/> the voice agent will drive for a simple turn: the local
    /// default first (what a "Hi" routes to), then cloud, then the offline stand-in. Mirrors
    /// <c>AdaAgentFactory</c>'s resolution so the probe tests the real thing, not an approximation.
    /// </summary>
    public static IChatClient ResolveModelClient(ProviderRegistry registry, AdaModelOptions options)
        => registry.CreateForRole(ModelRole.Default)
           ?? ModelClientFactory.Create(options)
           ?? registry.CreateForRole(ModelRole.Escalation)
           ?? new StubChatClient();

    /// <summary>
    /// Probe a chat client with a hard timeout — the check that turns a silent "Hi → stuck on thinking"
    /// into a clear verdict. A real (non-stub) client must return within the cap or the voice turn hangs.
    /// </summary>
    public static async Task<PreflightCheck> ProbeModelAsync(IChatClient client, TimeSpan timeout, CancellationToken ct = default)
    {
        const string name = "Agent model responds";

        if (client is StubChatClient)
            return new(name, PreflightStatus.Warn,
                "No model configured — voice will reply with the offline stand-in. Set one up:  ada ollama setup");

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(timeout);
        var sw = Stopwatch.StartNew();
        try
        {
            // One token is enough to prove the model answers; we only care that bytes come back in time.
            _ = await client.GetResponseAsync(
                [new ChatMessage(ChatRole.User, "Reply with the single word: ok")],
                new ChatOptions { MaxOutputTokens = 1 },
                cts.Token).ConfigureAwait(false);
            sw.Stop();
            var ms = sw.ElapsedMilliseconds;
            return ms > 4000
                ? new(name, PreflightStatus.Warn,
                    $"Responded, but slowly ({ms} ms). A cold first turn this slow can exceed the {timeout.TotalSeconds:0}s voice cap — warm the model once before speaking.")
                : new(name, PreflightStatus.Ok, $"Responded in {ms} ms.");
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            return new(name, PreflightStatus.Fail,
                $"No response within {timeout.TotalSeconds:0}s — a voice turn will hang on \"thinking\". If you use a local runtime, check it is running:  ada ollama status");
        }
        catch (Exception ex)
        {
            return new(name, PreflightStatus.Fail, $"Model call failed: {ex.Message}");
        }
    }

    /// <summary>
    /// Fast, offline model-cache readiness: the selected STT model, the TTS voice + engine, and the Silero
    /// VAD. No network, no model probe — safe to run at startup. Anything not cached is a warning (it
    /// downloads on first use), never a hard fail.
    /// </summary>
    public static IReadOnlyList<PreflightCheck> CheckSpeechModels(string sttModel, string ttsProvider, string ttsVoice)
    {
        var checks = new List<PreflightCheck>();
        foreach (var m in VoiceModels.Status(sttModel, ttsProvider, ttsVoice))
            checks.Add(new($"{m.Kind}: {m.Name}",
                m.Ready ? PreflightStatus.Ok : PreflightStatus.Warn,
                m.Ready ? "Cached." : $"Not downloaded (~{m.ApproxMb} MB) — warm it in Settings → Voice, or it fetches on first use."));
        checks.Add(CheckSileroVad());
        return checks;
    }

    /// <summary>
    /// The Silero VAD ONNX is not in Ada's warm-up catalog — Voxa downloads it internally on the first
    /// voice turn — so check the model cache directly. Best-effort and never fatal: it self-heals with one
    /// online turn, but flagging it warns about an offline first run (which would get no real VAD).
    /// </summary>
    private static PreflightCheck CheckSileroVad()
    {
        const string name = "Voice activity detector: Silero VAD";
        try
        {
            var cache = new VoxaModelCache(new VoxaModelCacheOptions(VoxaModelCacheOptions.ResolveCacheRoot(), Offline: true));
            var cached = cache.Enumerate().Any(m => m.Id.Contains("silero", StringComparison.OrdinalIgnoreCase));
            return cached
                ? new(name, PreflightStatus.Ok, "Cached.")
                : new(name, PreflightStatus.Warn,
                    "Not cached yet — Voxa downloads it on the first voice turn (needs network once). An offline first run has no VAD.");
        }
        catch (Exception ex)
        {
            return new(name, PreflightStatus.Warn, $"Could not check the VAD cache: {ex.Message}");
        }
    }

    /// <summary>
    /// The full readiness report: offline speech-model checks plus a live model probe. Resolves the model
    /// client from <paramref name="services"/> so it tests exactly what the running voice agent uses.
    /// </summary>
    public static async Task<PreflightReport> RunAsync(IServiceProvider services, string sttModel, string ttsProvider, string ttsVoice,
        TimeSpan? probeTimeout = null, CancellationToken ct = default)
    {
        var checks = new List<PreflightCheck>(CheckSpeechModels(sttModel, ttsProvider, ttsVoice));

        var registry = services.GetRequiredService<ProviderRegistry>();
        var options = services.GetService<AdaModelOptions>() ?? AdaModelOptions.FromEnvironment();

        IChatClient client;
        try { client = ResolveModelClient(registry, options); }
        catch (Exception ex)
        {
            // e.g. an ONNX provider whose model isn't downloaded throws here with a "ada model pull" hint.
            checks.Add(new("Agent model responds", PreflightStatus.Fail, $"Could not build the model client: {ex.Message}"));
            return new PreflightReport(checks);
        }

        try { checks.Add(await ProbeModelAsync(client, probeTimeout ?? DefaultProbeTimeout, ct).ConfigureAwait(false)); }
        finally { (client as IDisposable)?.Dispose(); }

        return new PreflightReport(checks);
    }
}

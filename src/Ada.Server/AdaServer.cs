using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Ada.Core;
using Ada.Tools;
using Ada.Voice;

namespace Ada.Server;

/// <summary>Options for the loopback host. Port 0 = an OS-assigned ephemeral port.
/// <paramref name="Voice"/> mounts the Voxa voice pipeline (opt-in; loads local speech models).</summary>
public sealed record AdaServerOptions(int Port = 0, bool Voice = false);

/// <summary>
/// A started loopback server. Exposes only its bound <see cref="Url"/> (always 127.0.0.1) so
/// hosts — the tray app, the CLI — never touch ASP.NET types and "no public socket" holds.
/// </summary>
public sealed class RunningAdaServer : IAsyncDisposable
{
    private readonly WebApplication _app;
    internal RunningAdaServer(WebApplication app, string url) { _app = app; Url = url; }

    public string Url { get; }
    public Task StopAsync(CancellationToken ct = default) => _app.StopAsync(ct);
    public Task WaitForShutdownAsync(CancellationToken ct = default) => _app.WaitForShutdownAsync(ct);
    public async ValueTask DisposeAsync() => await _app.DisposeAsync();
}

/// <summary>
/// Builds Ada's in-process web surface: a Kestrel server bound to loopback only, serving the
/// WebView2 chat UI and the streaming <c>/api/chat</c> endpoint. The same host backs both the
/// tray app and the CLI, so the M0 acceptance criteria (echo round-trip, loopback-only) are
/// verifiable headlessly.
/// </summary>
public static class AdaServer
{
    public static WebApplication Build(AdaServerOptions options)
    {
        var builder = WebApplication.CreateSlimBuilder();

        // Loopback only — 127.0.0.1. Ada never binds a public interface (egress contract, spec §14).
        builder.WebHost.UseUrls($"http://127.0.0.1:{options.Port}");
        // Quiet by default; set ADA_LOG=Information|Debug to surface the Voxa voice pipeline for diagnostics.
        var logLevel = Environment.GetEnvironmentVariable("ADA_LOG") is { Length: > 0 } lv
            && Enum.TryParse<Microsoft.Extensions.Logging.LogLevel>(lv, true, out var parsed)
            ? parsed : Microsoft.Extensions.Logging.LogLevel.Warning;
        builder.Logging.SetMinimumLevel(logLevel);

        // The GUI uses interactive approval cards; register it before AddAda so the harness keeps it.
        builder.Services.AddSingleton<InteractiveApprovalHandler>();
        builder.Services.AddSingleton<IApprovalHandler>(sp => sp.GetRequiredService<InteractiveApprovalHandler>());
        builder.Services.AddAda();

        // Bring the AIO sandbox up in the background and mount its /mcp tools (host fallback if absent).
        builder.Services.AddHostedService<SandboxHostedService>();

        var voiceReady = false;
        if (options.Voice)
        {
            try { AdaVoice.AddAdaVoice(builder); voiceReady = true; }
            catch (Exception ex) { Console.Error.WriteLine($"[voice] disabled: {ex.Message}"); }
        }

        var app = builder.Build();
        AdaApi.Map(app);

        if (voiceReady)
        {
            try { AdaVoice.MapAdaVoice(app); }
            catch (Exception ex) { Console.Error.WriteLine($"[voice] endpoint not mapped: {ex.Message}"); }

            // Fast, offline readiness hint in the log so a half-configured voice setup is visible up front.
            // (The agent's model is probed on demand — `ada doctor` / GET /api/voice/preflight — not here,
            // since a real generation can be slow and must never block startup.)
            try
            {
                var vcfg = new ConfigStore().Load();
                var notReady = VoicePreflight.CheckSpeechModels(vcfg.SttModel, vcfg.TtsProvider, vcfg.TtsVoice)
                    .Where(c => c.Status != PreflightStatus.Ok).ToList();
                var log = app.Services.GetRequiredService<ILoggerFactory>().CreateLogger("Ada.Voice");
                if (notReady.Count == 0)
                    log.LogInformation("Voice ready: speech models cached. Probe the model with `ada doctor`.");
                else
                    log.LogWarning("Voice enabled, but {Count} speech model(s) not cached: {Models}. Warm up in Settings → Voice, or run `ada doctor`.",
                        notReady.Count, string.Join("; ", notReady.Select(c => c.Name)));
            }
            catch { /* readiness logging is best-effort */ }
        }

        return app;
    }

    /// <summary>Starts the server and returns once it is listening, with the bound URL resolved.</summary>
    public static async Task<RunningAdaServer> StartAsync(AdaServerOptions options, CancellationToken ct = default)
    {
        var app = Build(options);
        await app.StartAsync(ct);
        var url = app.Urls.First();
        return new RunningAdaServer(app, url);
    }

    /// <summary>Starts and blocks until shutdown. <paramref name="onStarted"/> receives the bound URL.</summary>
    public static async Task RunAsync(AdaServerOptions options, Action<string>? onStarted = null, CancellationToken ct = default)
    {
        await using var server = await StartAsync(options, ct);
        onStarted?.Invoke(server.Url);
        await server.WaitForShutdownAsync(ct);
    }
}

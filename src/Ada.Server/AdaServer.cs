using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Ada.Core;
using Ada.Tools;

namespace Ada.Server;

/// <summary>Options for the loopback host. Port 0 = an OS-assigned ephemeral port.</summary>
public sealed record AdaServerOptions(int Port = 0);

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
        builder.Logging.SetMinimumLevel(Microsoft.Extensions.Logging.LogLevel.Warning);

        // The GUI uses interactive approval cards; register it before AddAda so the harness keeps it.
        builder.Services.AddSingleton<InteractiveApprovalHandler>();
        builder.Services.AddSingleton<IApprovalHandler>(sp => sp.GetRequiredService<InteractiveApprovalHandler>());
        builder.Services.AddAda();

        var app = builder.Build();
        AdaApi.Map(app);
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

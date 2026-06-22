using Ada.Core;
using Ada.Server;

namespace Ada.App;

internal static class Program
{
    /// <summary>
    /// Ada starts hidden as a tray companion. It hosts the loopback server in-process (the same
    /// server the CLI runs), then hands the bound URL to the tray context, which owns the global
    /// hotkey and the WebView2 window.
    /// </summary>
    [STAThread]
    private static void Main(string[] args)
    {
        ApplicationConfiguration.Initialize();

        // Central crash logging: route unhandled exceptions to the same %APPDATA%\Ada\logs file as the
        // server, so a GUI crash is never silent. (Set ADA_LOG=Debug for full voice-pipeline tracing.)
        var logPath = AdaPaths.LogFilePath();
        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
            FileLoggerProvider.Append(logPath, $"{DateTime.UtcNow:HH:mm:ss.fff} CRT AppDomain — unhandled exception{Environment.NewLine}{e.ExceptionObject}");
        TaskScheduler.UnobservedTaskException += (_, e) =>
            { FileLoggerProvider.Append(logPath, $"{DateTime.UtcNow:HH:mm:ss.fff} ERR Task — unobserved exception{Environment.NewLine}{e.Exception}"); e.SetObserved(); };
        Application.ThreadException += (_, e) =>
            FileLoggerProvider.Append(logPath, $"{DateTime.UtcNow:HH:mm:ss.fff} CRT UI thread — unhandled exception{Environment.NewLine}{e.Exception}");
        Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException);

        var server = AdaServer.StartAsync(new AdaServerOptions(Port: 0, Voice: true)).GetAwaiter().GetResult();

        // A manual launch (double-click) opens the window and runs the startup splash; autostart-at-login
        // passes --tray so Ada stays quietly in the tray until the user summons her.
        var startHidden = args.Any(a => string.Equals(a, "--tray", StringComparison.OrdinalIgnoreCase));

        // If Ollama is the chosen local runtime, bring it up now (no silent download — the wizard owns
        // that). Single-flight and observable via /api/launch, so the splash shows "starting → ready"
        // instead of the first turn hanging on a refused connection. Adopts a user's own Ollama as-is.
        OllamaLauncher.EnsureStarted();

        try
        {
            using var ctx = new AdaApplicationContext(server.Url, showOnLaunch: !startHidden);
            Application.Run(ctx);
        }
        finally
        {
            OllamaLauncher.ShutdownAsync().AsTask().GetAwaiter().GetResult();
            server.DisposeAsync().AsTask().GetAwaiter().GetResult();
        }
    }
}

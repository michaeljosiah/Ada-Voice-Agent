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
    private static void Main()
    {
        ApplicationConfiguration.Initialize();

        var server = AdaServer.StartAsync(new AdaServerOptions(Port: 0, Voice: true)).GetAwaiter().GetResult();
        try
        {
            using var ctx = new AdaApplicationContext(server.Url);
            Application.Run(ctx);
        }
        finally
        {
            server.DisposeAsync().AsTask().GetAwaiter().GetResult();
        }
    }
}

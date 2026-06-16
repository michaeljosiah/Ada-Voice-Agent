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
    private static void Main()
    {
        ApplicationConfiguration.Initialize();

        var server = AdaServer.StartAsync(new AdaServerOptions(Port: 0, Voice: true)).GetAwaiter().GetResult();

        // If Ollama is the chosen local runtime, bring it up in the background (no silent download —
        // the setup wizard does that). Adopts an already-running Ollama without taking ownership.
        OllamaRuntime? ollama = null;
        var ollamaStartup = Task.Run(async () =>
        {
            if (new ConfigStore().Load().LocalRuntime == "ollama")
                try { ollama = await OllamaRuntime.StartAsync(new OllamaOptions(), allowDownload: false); } catch { /* optional */ }
        });

        try
        {
            using var ctx = new AdaApplicationContext(server.Url);
            Application.Run(ctx);
        }
        finally
        {
            try { ollamaStartup.Wait(2000); } catch { /* ignore */ }
            if (ollama is not null) ollama.DisposeAsync().AsTask().GetAwaiter().GetResult();
            server.DisposeAsync().AsTask().GetAwaiter().GetResult();
        }
    }
}

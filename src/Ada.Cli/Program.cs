using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Ada.Core;
using Ada.Server;

namespace Ada.Cli;

/// <summary>
/// Ada's headless test harness. The GUI shell (tray + WebView2) can't be driven without a desktop
/// session, so the CLI exercises the same Ada.Core engine and loopback server directly — proving
/// each milestone's logic from the terminal.
/// </summary>
internal static class Program
{
    private static int _failures;

    private static async Task<int> Main(string[] args)
    {
        var cmd = args.Length > 0 ? args[0].ToLowerInvariant() : "help";
        var rest = args.Skip(1).ToArray();
        return cmd switch
        {
            "chat" => await Chat(rest),
            "serve" => await Serve(rest),
            "selftest" => await SelfTest(),
            _ => Help(),
        };
    }

    /// <summary>Talk to Ada's engine directly (no HTTP) — the simplest proof of the turn path.</summary>
    private static async Task<int> Chat(string[] args)
    {
        var message = string.Join(' ', args);
        if (string.IsNullOrWhiteSpace(message)) { Console.Error.WriteLine("usage: ada chat <message>"); return 2; }

        IAdaEngine engine = new EchoEngine();
        var route = "local";
        await foreach (var chunk in engine.RespondAsync(new AdaRequest(message)))
        {
            Console.Write(chunk.Text);
            if (chunk.IsFinal) route = chunk.Route;
        }
        Console.WriteLine();
        Console.WriteLine($"[route: {route}]");
        return 0;
    }

    /// <summary>Run the loopback server in the foreground (what the tray app hosts in-process).</summary>
    private static async Task<int> Serve(string[] args)
    {
        var port = 0;
        for (var i = 0; i < args.Length - 1; i++)
            if (args[i] is "--port" or "-p" && int.TryParse(args[i + 1], out var p)) port = p;

        using var cts = new CancellationTokenSource();
        Console.CancelKeyPress += (_, e) => { e.Cancel = true; cts.Cancel(); };
        try
        {
            await AdaServer.RunAsync(
                new AdaServerOptions(Port: port),
                onStarted: url => Console.WriteLine($"Ada loopback server listening on {url}  (Ctrl+C to stop)"),
                ct: cts.Token);
        }
        catch (OperationCanceledException) { }
        return 0;
    }

    /// <summary>The M0 acceptance gate, headless: loopback-only bind + echo round-trip over /api/chat.</summary>
    private static async Task<int> SelfTest()
    {
        Console.WriteLine("Ada M0 self-test");
        await using var server = await AdaServer.StartAsync(new AdaServerOptions(Port: 0));
        Console.WriteLine($"  server url : {server.Url}");

        var uri = new Uri(server.Url);
        Report("binds loopback only (no public socket)", uri.Host is "127.0.0.1" or "::1" or "localhost");

        using var http = new HttpClient { BaseAddress = uri };

        var health = await http.GetStringAsync("/healthz");
        Report("/healthz responds ok", health.Contains("\"status\":\"ok\""));

        var index = await http.GetStringAsync("/");
        Report("chat UI served at /", index.Contains("Ada", StringComparison.OrdinalIgnoreCase));

        var (reply, route) = await PostChat(http, "hello world");
        Console.WriteLine($"  reply      : \"{reply}\"");
        Console.WriteLine($"  route      : {route}");
        Report("echo round-trip over /api/chat", reply.StartsWith("Ada (echo):") && reply.Contains("hello world"));
        Report("route badge reported", route == "echo");

        var ok = _failures == 0;
        Console.WriteLine(ok ? "\nM0 SELF-TEST PASSED" : $"\nM0 SELF-TEST FAILED ({_failures} failure(s))");
        return ok ? 0 : 1;
    }

    private static async Task<(string reply, string route)> PostChat(HttpClient http, string message)
    {
        using var req = new HttpRequestMessage(HttpMethod.Post, "/api/chat")
        {
            Content = JsonContent.Create(new { message }),
        };
        using var resp = await http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead);
        resp.EnsureSuccessStatusCode();

        await using var stream = await resp.Content.ReadAsStreamAsync();
        using var reader = new StreamReader(stream);
        var sb = new StringBuilder();
        var route = string.Empty;
        var ev = "message";
        string? line;
        while ((line = await reader.ReadLineAsync()) is not null)
        {
            if (line.StartsWith("event:", StringComparison.Ordinal))
                ev = line["event:".Length..].Trim();
            else if (line.StartsWith("data:", StringComparison.Ordinal))
            {
                using var doc = JsonDocument.Parse(line["data:".Length..].Trim());
                if (ev == "chunk" && doc.RootElement.TryGetProperty("text", out var t))
                    sb.Append(t.GetString());
                else if (ev == "done" && doc.RootElement.TryGetProperty("route", out var r))
                    route = r.GetString() ?? string.Empty;
            }
        }
        return (sb.ToString(), route);
    }

    private static void Report(string name, bool ok)
    {
        if (!ok) _failures++;
        Console.WriteLine($"  [{(ok ? "PASS" : "FAIL")}] {name}");
    }

    private static int Help()
    {
        Console.WriteLine("""
            ada — Ada Voice Agent CLI (test harness)

            usage:
              ada chat <message>     talk to the engine directly (prints the streamed reply)
              ada serve [--port N]   run the loopback web server (default: ephemeral port)
              ada selftest           run the current milestone's headless acceptance checks
            """);
        return 0;
    }
}

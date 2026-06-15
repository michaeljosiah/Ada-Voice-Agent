using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Ada.Core;
using Ada.Server;
using Ada.Tools;
using Microsoft.Extensions.DependencyInjection;

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

    /// <summary>Talk to Ada's configured engine (echo, or a real local model via ADA_* env vars).</summary>
    private static async Task<int> Chat(string[] args)
    {
        var message = string.Join(' ', args);
        if (string.IsNullOrWhiteSpace(message)) { Console.Error.WriteLine("usage: ada chat <message>"); return 2; }

        using var provider = new ServiceCollection().AddAdaCore().BuildServiceProvider();
        var engine = provider.GetRequiredService<IAdaEngine>();

        var route = "local";
        try
        {
            await foreach (var chunk in engine.RespondAsync(new AdaRequest(message)))
            {
                Console.Write(chunk.Text);
                route = chunk.Route;
            }
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"\n[could not reach the model: {ex.Message}]");
            return 1;
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

    /// <summary>The cumulative headless acceptance gate for every milestone shipped so far.</summary>
    private static async Task<int> SelfTest()
    {
        Console.WriteLine("Ada self-test (M0–M2)\n");

        // ---- M0: loopback server + streaming round-trip ----
        await using var server = await AdaServer.StartAsync(new AdaServerOptions(Port: 0));
        var uri = new Uri(server.Url);
        Report("M0 server binds loopback only (no public socket)", uri.Host is "127.0.0.1" or "::1" or "localhost");

        using var http = new HttpClient { BaseAddress = uri };
        Report("M0 /healthz responds ok", (await http.GetStringAsync("/healthz")).Contains("\"status\":\"ok\""));
        Report("M0 chat UI served at /", (await http.GetStringAsync("/")).Contains("Ada", StringComparison.OrdinalIgnoreCase));

        var (sReply, sRoute) = await PostChat(http, "hello world");
        Report("M0 turn streams over /api/chat", sReply.Length > 0 && sRoute.Length > 0);
        Console.WriteLine($"       server reply: \"{sReply}\" [{sRoute}]");

        // ---- M0: echo engine ----
        var echo = await Collect(new EchoEngine(), "hello world");
        Report("M0 echo engine echoes input, route 'echo'", echo.text.Contains("hello world") && echo.route == "echo");

        // ---- M1: real agent path over a stub model (no model / keys / network) ----
        var agent = new AgentEngine(new StubChatClient(), Persona.Load(), route: "local");
        var ar = await Collect(agent, "remember the milk");
        Report("M1 agent engine streams a reply", ar.text.Length > 0);
        Report("M1 agent engine routes 'local'", ar.route == "local");
        Report("M1 agent passes the user message to the model", ar.text.Contains("remember the milk"));
        Console.WriteLine($"       agent reply : \"{ar.text}\" [{ar.route}]");

        // ---- M1: provider wiring ----
        Report("M1 echo provider yields no chat client", ModelClientFactory.Create(new AdaModelOptions { Provider = "echo" }) is null);
        Report("M1 local provider builds a chat client", ModelClientFactory.Create(
            new AdaModelOptions { Provider = "openai-compatible", Endpoint = "http://localhost:11434/v1", ModelId = "m" }) is not null);

        // ---- M2: the safety floor (scope, approval, audit, sandbox) ----
        var allowed = Directory.CreateTempSubdirectory("ada_st_").FullName;
        try
        {
            var scope = new ScopePolicy([allowed], [], [Path.Combine(allowed, "secrets")]);

            var audit = new InMemoryAuditLog();
            var fsApprove = new FileSystemTools(new ToolContext(scope, new AutoApprovalHandler(approveMutations: true), audit));
            var okPath = Path.Combine(allowed, "note.txt");
            await fsApprove.WriteFile(okPath, "hi");
            Report("M2 approved write lands on disk and is audited",
                File.Exists(okPath) && audit.Recent().Any(e => e is { Tool: "write_file", Outcome: "executed" }));

            var fsDeny = new FileSystemTools(new ToolContext(scope, new AutoApprovalHandler(approveMutations: false), new InMemoryAuditLog()));
            var denyPath = Path.Combine(allowed, "blocked.txt");
            var denyResult = await fsDeny.WriteFile(denyPath, "x");
            Report("M2 denied write never touches disk", !File.Exists(denyPath) && denyResult.Contains("Denied"));

            var escape = await fsApprove.WriteFile(Path.Combine(Path.GetTempPath(), "ada_escape.txt"), "x");
            Report("M2 write outside allowed roots is blocked even if approved", escape.Contains("Blocked"));

            var box = new WasmCodeSandbox();
            var compute = await box.RunAsync(new SandboxRequest("wat", "(module (func (export \"run\") (result i32) i32.const 42))"));
            Report("M2 wasm sandbox runs an isolated module", compute is { Ok: true, Output: "42" });
            var runaway = await box.RunAsync(new SandboxRequest("wat",
                "(module (func (export \"run\") (result i32) (loop (br 0)) (i32.const 0)))", Fuel: 100_000));
            Report("M2 wasm runaway traps on fuel (no hang)", runaway is { Ok: false, Reason: "trapped-fuel" });

            // interactive approval round-trip — the GUI card flow, exercised without a UI
            var interactive = new InteractiveApprovalHandler();
            var pending = interactive.RequestApprovalAsync(new ApprovalRequest("write_file", RiskTier.Low, "Write a file", @"C:\work\y.txt"));
            Report("M2 interactive approval surfaces the literal detail", interactive.Pending_.Any(p => p.Detail == @"C:\work\y.txt"));
            interactive.Resolve(interactive.Pending_.First().Id, ApprovalDecision.Approve());
            Report("M2 interactive approval resolves to the user's decision", (await pending).Approved);
        }
        finally { try { Directory.Delete(allowed, true); } catch { /* best effort */ } }

        var ok = _failures == 0;
        Console.WriteLine(ok ? "\nSELF-TEST PASSED" : $"\nSELF-TEST FAILED ({_failures} failure(s))");
        return ok ? 0 : 1;
    }

    private static async Task<(string text, string route)> Collect(IAdaEngine engine, string message)
    {
        var sb = new StringBuilder();
        var route = string.Empty;
        await foreach (var chunk in engine.RespondAsync(new AdaRequest(message)))
        {
            sb.Append(chunk.Text);
            route = chunk.Route;
        }
        return (sb.ToString(), route);
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
              ada chat <message>     talk to the configured engine (echo, or a local model)
              ada serve [--port N]   run the loopback web server (default: ephemeral port)
              ada selftest           run the cumulative headless acceptance checks

            model config (env):
              ADA_PROVIDER=openai-compatible   ADA_ENDPOINT=http://localhost:11434/v1
              ADA_MODEL=qwen2.5:7b-instruct     ADA_API_KEY=(optional)
            """);
        return 0;
    }
}

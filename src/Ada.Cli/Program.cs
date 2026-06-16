using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Ada.Core;
using Ada.Server;
using Ada.Tools;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
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
            "auth" => Auth(rest),
            "providers" => Providers(),
            "route" => RouteCmd(rest),
            "memory" => Memory(rest),
            "skills" => Skills(rest),
            "mcp" => await Mcp(rest),
            "jobs" => Jobs(rest),
            "run-due" => await RunDue(),
            "config" => Config(rest),
            "doctor" => Doctor(),
            "model" => await Model(rest),
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
        Console.WriteLine("Ada self-test (M0–M8)\n");

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

        // ---- M3: providers, vault, hybrid routing ----
        var vault = new InMemoryCredentialVault();
        vault.Set("provider:test", "sk-secret");
        Report("M3 vault round-trips a secret", vault.Get("provider:test") == "sk-secret");

        var provPath = Path.Combine(Path.GetTempPath(), $"ada_prov_{Guid.NewGuid():n}.json");
        try
        {
            var store = new ProviderStore(provPath);
            store.Upsert(new ProviderConfig("local", ProviderKind.OpenAiCompatible, "qwen2.5:7b", "http://localhost:11434/v1", AuthMethod.None, ModelRole.Default));
            store.Upsert(new ProviderConfig("anthropic", ProviderKind.Anthropic, "claude-sonnet-4-6", "https://api.anthropic.com/v1", AuthMethod.ApiKey, ModelRole.Escalation));
            Report("M3 provider store persists and reloads", new ProviderStore(provPath).Load().Count == 2);

            var policy = new RoutingPolicy(hasEscalation: true, stayLocal: false, "local", "anthropic");
            Report("M3 simple turn stays local", policy.Route([new ChatMessage(ChatRole.User, "what's the weather?")], null).Role == ModelRole.Default);
            Report("M3 code task escalates", policy.Route([new ChatMessage(ChatRole.User, "refactor this function")], null).Role == ModelRole.Escalation);
            Report("M3 stay-local override pins to local",
                new RoutingPolicy(true, true, "local", "anthropic").Route([new ChatMessage(ChatRole.User, "refactor this")], null).Role == ModelRole.Default);

            var egressAudit = new InMemoryAuditLog();
            var hybrid = new HybridChatClient(new StubChatClient(), new StubChatClient(), new RoutingPolicy(true, false, "local", "anthropic"), egressAudit);
            await Drain(hybrid.GetStreamingResponseAsync([new ChatMessage(ChatRole.User, "implement an algorithm")]));
            Report("M3 hybrid escalates and logs egress", hybrid.CurrentRoute.StartsWith("anthropic") && egressAudit.Recent().Any(e => e.Outcome == "escalated"));

            using var degraded = new ServiceCollection()
                .AddSingleton(new ProviderStore(provPath + ".empty"))
                .AddSingleton<ICredentialVault>(new InMemoryCredentialVault())
                .AddAdaCore(new AdaModelOptions { Provider = "echo" })
                .BuildServiceProvider();
            Report("M3 no cloud provider -> still runs locally (echo), never broken",
                degraded.GetRequiredService<IAdaEngine>() is EchoEngine);
        }
        finally { try { File.Delete(provPath); } catch { /* best effort */ } }

        // ---- M4: memory + compaction ----
        var memDir = Directory.CreateTempSubdirectory("ada_mem_").FullName;
        try
        {
            string slug;
            using (var mem = new FileMemoryStore(memDir))
            {
                var e = mem.Remember("Accountant Tunde", "accountant; year-end 31 March", MemoryType.Reference, "Tunde is my accountant. Year-end is 31 March.");
                slug = e.Name;
                Report("M4 remember writes a readable file", File.Exists(Path.Combine(memDir, slug + ".md")));
                Report("M4 MEMORY.md index gains a line", mem.IndexMarkdown().Contains(slug));
                Report("M4 FTS5 recall finds the memory", mem.Recall("when is my accountant's year end").Any(h => h.Name == slug));
            }
            using (var mem2 = new FileMemoryStore(memDir))
                Report("M4 a new session recalls remembered facts", mem2.Recall("accountant").Any(h => h.Name == slug));
        }
        finally { try { Directory.Delete(memDir, true); } catch { /* best effort */ } }

        var longHistory = new List<ChatMessage>();
        for (var i = 0; i < 40; i++)
        {
            longHistory.Add(new ChatMessage(ChatRole.User, new string('x', 400)));
            longHistory.Add(new ChatMessage(ChatRole.Assistant, new string('y', 400)));
        }
        var compacted = await new LengthCompactionStrategy(maxChars: 4000, keepRecent: 6).CompactAsync(longHistory);
        Report("M4 compaction bounds a long session without losing recent turns",
            longHistory.Count == 80 && compacted.Count <= 7 && compacted[0].Role == ChatRole.System);

        // ---- M5: skills + MCP gating + sandbox zones ----
        var composedAgent = SkillComposer.Compose(new Persona("BASE"), [], [new ResearchSkill(new WebTools(new InMemoryAuditLog()))]);
        Report("M5 enabling a skill adds its instruction", composedAgent.Instructions.Contains("synthesize"));
        Report("M5 enabling a skill adds its tools", composedAgent.Tools.Any(t => t.Name == "web_fetch"));

        var skillPath = Path.Combine(Path.GetTempPath(), $"ada_skills_{Guid.NewGuid():n}.json");
        try
        {
            ISkill[] skills = [new ResearchSkill(new WebTools(new InMemoryAuditLog())), new FinanceRecordsSkill()];
            var reg = new SkillRegistry(skills, skillPath);
            Report("M5 research on by default, finance-records off (external seam)", reg.IsEnabled("research") && !reg.IsEnabled("finance-records"));
            reg.Enable("finance-records");
            Report("M5 skill enable persists across sessions", new SkillRegistry(skills, skillPath).IsEnabled("finance-records"));
        }
        finally { try { File.Delete(skillPath); } catch { /* best effort */ } }

        var finance = new FinanceRecordsSkill();
        Report("M5 finance seam: egress mount + describe-not-prescribe + original currency",
            finance.Mcp!.IsEgress && finance.InstructionFragment!.Contains("never convert") && finance.InstructionFragment.Contains("Describe, never prescribe"));

        var gatedDeny = new GatedAIFunction(AIFunctionFactory.Create((string x) => $"ran:{x}", "echo"), () => Task.FromResult(false));
        Report("M5 a write-capable MCP tool is gated (deny blocks it)",
            (await gatedDeny.InvokeAsync(new AIFunctionArguments { ["x"] = "y" }))?.ToString()?.Contains("Denied") == true);

        var container = new ContainerCodeSandbox();
        Report("M5 container zone rejects work it can't run cleanly", await container.RunAsync(new SandboxRequest("ruby", "puts 1")) is { Ok: false });
        Console.WriteLine($"       Zone 2 (Docker container) available: {container.Available}");
        if (container.Available)
        {
            var run = await container.RunAsync(new SandboxRequest("python", "print(40+2)"));
            Console.WriteLine($"       Zone 2 python run -> ok={run.Ok} output=\"{run.Output}\" reason={run.Reason}");
        }

        using var webTools = new WebTools(new InMemoryAuditLog());
        var fetched = await webTools.WebFetch(server.Url + "/healthz");
        Report("M5 web_fetch retrieves content (egress) from the loopback", fetched.Contains("\"status\":\"ok\""));

        // ---- M6: voice agent composition (the AIAgent Voxa drives) ----
        using (var voiceProvider = new ServiceCollection()
            .AddSingleton(new ProviderStore(provPath + ".voice"))
            .AddSingleton<ICredentialVault>(new InMemoryCredentialVault())
            .AddAda(new AdaModelOptions { Provider = "echo" })
            .BuildServiceProvider())
        {
            var voiceAgent = voiceProvider.GetService<AIAgent>();
            Report("M6 voice agent composes (same persona/skills/tools as the text surface)", voiceAgent is not null);
        }

        // ---- M7: automations & schedules ----
        Report("M7 natural-language schedule -> cron", NlCron.ToCron("every weekday at 8") == "0 8 * * 1-5");
        Report("M7 a job missed while Ada was closed is due (catch-up)",
            JobRunner.IsDue(new ScheduledJob("1", "b", "* * * * *", "x", LastRunUtc: DateTime.UtcNow.AddMinutes(-5)), DateTime.UtcNow));

        var jobsDir = Directory.CreateTempSubdirectory("ada_jobs_").FullName;
        try
        {
            var jstore = new JobStore(Path.Combine(jobsDir, "jobs.json"));
            jstore.Upsert(new ScheduledJob("1", "morning-brief", "* * * * *", "what's today?", DeliveryTarget.Note, LastRunUtc: DateTime.UtcNow.AddMinutes(-5)));
            var ndelivery = new NoteDeliveryService(jobsDir);
            var jaudit = new InMemoryAuditLog();
            var jkill = new KillSwitch(Path.Combine(jobsDir, "paused"));

            var ran = await new JobRunner(jstore, ndelivery, jaudit, jkill).RunDueAsync(DateTime.UtcNow, new EchoEngine());
            Report("M7 run-due runs the job, delivers a note, tags the audit log",
                ran == 1 && File.Exists(ndelivery.PathFor(jstore.Load()[0])) && jaudit.Recent().Any(e => e.Outcome.Contains("autonomous")));

            jkill.Paused = true;
            Report("M7 one kill switch pauses all jobs",
                await new JobRunner(jstore, ndelivery, jaudit, jkill).RunDueAsync(DateTime.UtcNow, new EchoEngine()) == 0);
        }
        finally { try { Directory.Delete(jobsDir, true); } catch { /* best effort */ } }

        var noGrant = await new UnattendedApprovalHandler([]).RequestApprovalAsync(new ApprovalRequest("write_file", RiskTier.Low, "w", "x"));
        var withGrant = await new UnattendedApprovalHandler([new StandingGrant("write_file", RiskTier.Low)]).RequestApprovalAsync(new ApprovalRequest("write_file", RiskTier.Low, "w", "x"));
        Report("M7 unattended runs read-only by default; standing grants are narrow", !noGrant.Approved && withGrant.Approved);

        const string testTask = "AdaJobRunnerSelfTest";
        var taskInstalled = WindowsJobRunnerTask.Install("cmd /c echo ada", everyMinutes: 60, taskName: testTask);
        Console.WriteLine($"       Windows Task Scheduler register/query/remove -> install={taskInstalled} exists={WindowsJobRunnerTask.Exists(testTask)} removed={WindowsJobRunnerTask.Uninstall(testTask)}");

        // ---- M8: ship — profiles, config, autostart, setup wizard ----
        Report("M8 Private profile stays local (no cloud)",
            Profiles.For(AdaProfile.Private) is { StayLocal: true, AllowCloudEscalation: false });
        Report("M8 Balanced profile enables cloud + container", Profiles.For(AdaProfile.Balanced).AllowCloudEscalation);

        var cfgPath = Path.Combine(Path.GetTempPath(), $"ada_cfg_{Guid.NewGuid():n}.json");
        try
        {
            new ConfigStore(cfgPath).Save(new AdaConfig { Profile = AdaProfile.Power, SetupComplete = true });
            Report("M8 config persists (no JSON editing needed)",
                new ConfigStore(cfgPath).Load() is { Profile: AdaProfile.Power, SetupComplete: true });
        }
        finally { try { File.Delete(cfgPath); } catch { /* best effort */ } }

        if (OperatingSystem.IsWindows())
        {
            var valueName = "AdaSelfTest_" + Guid.NewGuid().ToString("n")[..8];
            var autostartOk = Autostart.Enable(@"C:\Ada\ada.exe", valueName) && Autostart.IsEnabled(valueName)
                && Autostart.Disable(valueName) && !Autostart.IsEnabled(valueName);
            Report("M8 autostart registers + removes cleanly under the Run key", autostartOk);
        }

        var apiConfig = await http.GetStringAsync("/api/config");
        Report("M8 settings endpoint serves config + provider catalog", apiConfig.Contains("setupComplete") && apiConfig.Contains("catalog"));
        Report("M8 first-run setup wizard is served (no terminal needed)", (await http.GetStringAsync("/")).Contains("Welcome to Ada"));

        // ---- ONNX local model: in-process provider + first-run download ----
        Report("ONNX catalog includes a Gemma default", OnnxModelCatalog.Find(OnnxModelCatalog.DefaultModelId)?.Family == "gemma");
        var onnxClearFail = false;
        try { ModelClientFactory.Create(new AdaModelOptions { Provider = "onnx", ModelId = "not-downloaded" }); }
        catch (InvalidOperationException ex) { onnxClearFail = ex.Message.Contains("ada model pull"); }
        Report("ONNX provider without a model fails with a clear 'pull' hint", onnxClearFail);
        Report("ONNX models surfaced by the settings endpoint", (await http.GetStringAsync("/api/models")).Contains("gemma-3-1b"));

        var onnxTmp = Directory.CreateTempSubdirectory("ada_onnx_").FullName;
        var onnxDownloaded = false;
        try
        {
            await new HuggingFaceDownloader().DownloadAsync("smartvest-llc/gemma-3-1b-it-genai", "", ["genai_config.json"], onnxTmp);
            onnxDownloaded = File.Exists(Path.Combine(onnxTmp, "genai_config.json"));
        }
        catch (Exception ex) { Console.WriteLine($"       (ONNX live-download note: {ex.Message})"); }
        finally { try { Directory.Delete(onnxTmp, true); } catch { /* best effort */ } }
        Report("ONNX downloader fetches a real model file from Hugging Face", onnxDownloaded);

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

    // ---- M3: provider auth + routing commands ----

    private static int Auth(string[] args)
    {
        var sub = args.Length > 0 ? args[0].ToLowerInvariant() : "list";
        var store = new ProviderStore();
        var vault = new DpapiCredentialVault();

        switch (sub)
        {
            case "list":
                var providers = store.Load();
                if (providers.Count == 0)
                {
                    Console.WriteLine("No providers configured. Add one with:  ada auth login <id> --key <KEY>");
                    Console.WriteLine("Catalog ids: " + string.Join(", ", ProviderCatalog.BuiltIns.Select(b => b.Id)));
                    return 0;
                }
                foreach (var p in providers)
                {
                    var keyState = p.Auth == AuthMethod.None ? "none" : vault.Has(p.VaultKey) ? "vault" : "MISSING";
                    Console.WriteLine($"  {p.Id,-14} {p.Kind,-16} role={p.Role,-10} model={p.ModelId,-22} key={keyState}");
                }
                return 0;

            case "login":
                return Login(args.Skip(1).ToArray(), store, vault);

            case "logout":
                if (args.Length < 2) { Console.Error.WriteLine("usage: ada auth logout <id>"); return 2; }
                store.Remove(args[1]);
                vault.Delete($"provider:{args[1]}");
                Console.WriteLine($"Removed provider '{args[1]}'.");
                return 0;

            default:
                Console.Error.WriteLine("usage: ada auth [list | login <id> [--key ..] [--endpoint ..] [--model ..] [--kind ..] [--role ..] | logout <id>]");
                return 2;
        }
    }

    private static int Login(string[] args, ProviderStore store, ICredentialVault vault)
    {
        if (args.Length == 0)
        {
            Console.Error.WriteLine("usage: ada auth login <id|catalog-id> [--key KEY] [--endpoint URL] [--model M] [--kind K] [--role default|escalation]");
            return 2;
        }

        var id = args[0];
        var flags = ParseFlags(args.Skip(1));
        var catalog = ProviderCatalog.Find(id);
        try
        {
            var kind = flags.TryGetValue("kind", out var k) ? Enum.Parse<ProviderKind>(k, true) : catalog?.Kind ?? ProviderKind.OpenAiCompatible;
            var endpoint = flags.GetValueOrDefault("endpoint") ?? catalog?.DefaultEndpoint;
            var model = flags.GetValueOrDefault("model") ?? catalog?.DefaultModel
                ?? throw new ArgumentException("a --model is required for a custom provider");
            var role = flags.TryGetValue("role", out var r) ? Enum.Parse<ModelRole>(r, true)
                : catalog?.Auth == AuthMethod.None ? ModelRole.Default : ModelRole.Escalation;
            var auth = flags.ContainsKey("key") ? AuthMethod.ApiKey
                : catalog?.Auth ?? (kind == ProviderKind.AzureOpenAI ? AuthMethod.AzureCredential : AuthMethod.None);

            var config = new ProviderConfig(id, kind, model, endpoint, auth, role);
            store.Upsert(config);
            if (flags.TryGetValue("key", out var key)) vault.Set(config.VaultKey, key);

            Console.WriteLine($"Configured '{id}' — {kind}, role {role}, model {model}. Key in OS vault: {(flags.ContainsKey("key") ? "yes" : "no")}.");
            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"Could not configure provider: {ex.Message}");
            return 2;
        }
    }

    private static int Providers()
    {
        Console.WriteLine("Built-in provider catalog:");
        foreach (var e in ProviderCatalog.BuiltIns)
            Console.WriteLine($"  {e.Id,-14} {e.Label,-22} {e.Kind,-16} auth={e.Auth,-16} model={e.DefaultModel}");
        return 0;
    }

    private static int RouteCmd(string[] args)
    {
        var message = string.Join(' ', args);
        var registry = new ProviderRegistry(new ProviderStore(), new DpapiCredentialVault());
        var hasEscalation = registry.ForRole(ModelRole.Escalation) is not null && registry.ForRole(ModelRole.Default) is not null;
        var stayLocal = Environment.GetEnvironmentVariable("ADA_STAY_LOCAL") == "1";
        var escId = registry.ForRole(ModelRole.Escalation)?.Id ?? "cloud";

        var decision = new RoutingPolicy(hasEscalation, stayLocal, "local", escId)
            .Route([new ChatMessage(ChatRole.User, message)], null);
        Console.WriteLine($"route: {decision.Label}   (role={decision.Role}, reason={decision.Reason})");
        return 0;
    }

    private static async Task<int> Model(string[] args)
    {
        var sub = args.Length > 0 ? args[0].ToLowerInvariant() : "list";
        var store = new OnnxModelStore();

        switch (sub)
        {
            case "list":
                foreach (var m in OnnxModelCatalog.Models)
                    Console.WriteLine($"  [{(store.IsReady(m.Id) ? "downloaded" : "          ")}] {m.Id,-12} {m.Label}  ({m.License})");
                return 0;

            case "status":
                var downloaded = store.Downloaded();
                Console.WriteLine(downloaded.Count == 0 ? "No local models downloaded." : "Downloaded: " + string.Join(", ", downloaded));
                Console.WriteLine($"Active local model: {new ConfigStore().Load().LocalModelId ?? "(none — using echo/endpoint)"}");
                return 0;

            case "pull":
                if (args.Length < 2) { Console.Error.WriteLine("usage: ada model pull <id>   ids: " + string.Join(", ", OnnxModelCatalog.Models.Select(m => m.Id))); return 2; }
                var entry = OnnxModelCatalog.Find(args[1]);
                if (entry is null) { Console.Error.WriteLine($"Unknown model '{args[1]}'."); return 2; }
                Console.WriteLine($"Downloading {entry.Label} (~{entry.ApproxMb} MB) from {entry.Repo} …");
                var lastFile = string.Empty;
                var progress = new Progress<DownloadProgress>(p =>
                {
                    if (p.File != lastFile) { lastFile = p.File; Console.WriteLine($"  [{p.FileIndex}/{p.FileCount}] {p.File}"); }
                });
                try
                {
                    await store.DownloadAsync(entry, progress);
                    var cfg = new ConfigStore(); var c = cfg.Load(); c.LocalModelId = entry.Id; cfg.Save(c);
                    Console.WriteLine($"Done. '{entry.Id}' is now Ada's local model.");
                    return 0;
                }
                catch (Exception ex) { Console.Error.WriteLine($"Download failed: {ex.Message}"); return 1; }

            case "use" when args.Length >= 2:
                if (!store.IsReady(args[1])) { Console.Error.WriteLine($"Model '{args[1]}' is not downloaded. Run: ada model pull {args[1]}"); return 2; }
                var cf = new ConfigStore(); var cc = cf.Load(); cc.LocalModelId = args[1]; cf.Save(cc);
                Console.WriteLine($"Ada will use '{args[1]}' locally."); return 0;

            default:
                Console.Error.WriteLine("usage: ada model [list | pull <id> | use <id> | status]");
                return 2;
        }
    }

    private static int Config(string[] args)
    {
        var store = new ConfigStore();
        var cfg = store.Load();

        if (args.Length == 0)
        {
            Console.WriteLine($"  profile        : {cfg.Profile}");
            Console.WriteLine($"  setup complete : {cfg.SetupComplete}");
            Console.WriteLine($"  autostart      : {cfg.Autostart} (registry: {Autostart.IsEnabled()})");
            Console.WriteLine($"  hotkey         : {cfg.Hotkey}");
            return 0;
        }

        switch (args[0].ToLowerInvariant())
        {
            case "profile" when args.Length >= 2 && Enum.TryParse<AdaProfile>(args[1], true, out var p):
                cfg.Profile = p; store.Save(cfg); Console.WriteLine($"Profile set to {p}."); return 0;
            case "autostart" when args.Length >= 2:
                var on = args[1] is "on" or "true" or "1";
                var exe = Environment.ProcessPath ?? "ada";
                if (on) Autostart.Enable(exe); else Autostart.Disable();
                cfg.Autostart = on; store.Save(cfg);
                Console.WriteLine($"Autostart {(on ? "enabled" : "disabled")}."); return 0;
            default:
                Console.Error.WriteLine("usage: ada config [profile <Private|Balanced|Power> | autostart <on|off>]"); return 2;
        }
    }

    private static int Doctor()
    {
        Console.WriteLine("Ada doctor — readiness check\n");
        var providers = new ProviderStore().Load();
        Console.WriteLine($"  .NET runtime       : {Environment.Version}");
        Console.WriteLine($"  data directory     : {AdaPaths.DataDir}");
        Console.WriteLine($"  providers          : {(providers.Count == 0 ? "none (local/echo only)" : string.Join(", ", providers.Select(p => p.Id)))}");
        Console.WriteLine($"  container sandbox  : {(new ContainerCodeSandbox().Available ? "Docker present (Zone 2 ready)" : "Docker absent (Zone 1 only)")}");
        Console.WriteLine($"  scheduled jobs     : {new JobStore().Load().Count} (paused: {new KillSwitch().Paused})");
        Console.WriteLine($"  windows autostart  : {Autostart.IsEnabled()}");
        Console.WriteLine($"  setup complete     : {new ConfigStore().Load().SetupComplete}");
        return 0;
    }

    private static int Jobs(string[] args)
    {
        var sub = args.Length > 0 ? args[0].ToLowerInvariant() : "list";
        var store = new JobStore();
        switch (sub)
        {
            case "list":
                var jobs = store.Load();
                if (jobs.Count == 0) { Console.WriteLine("No scheduled jobs."); return 0; }
                foreach (var j in jobs)
                    Console.WriteLine($"  {j.Name,-20} {j.Cron,-14} -> {j.Delivery,-5} {(j.Enabled ? string.Empty : "[off] ")}last={(j.LastRunUtc?.ToString("u") ?? "never")}");
                return 0;
            case "add":
                if (args.Length < 2) { Console.Error.WriteLine("usage: ada jobs add <name> --when \"...\" --do \"...\" [--deliver note]"); return 2; }
                var flags = ParseFlags(args.Skip(2));
                var cron = NlCron.ToCron(flags.GetValueOrDefault("when", string.Empty));
                if (cron is null) { Console.Error.WriteLine("Could not parse --when into a schedule (try 'every weekday at 8')."); return 2; }
                var target = Enum.TryParse<DeliveryTarget>(flags.GetValueOrDefault("deliver", "note"), true, out var d) ? d : DeliveryTarget.Note;
                store.Upsert(new ScheduledJob(Guid.NewGuid().ToString("n"), args[1], cron, flags.GetValueOrDefault("do", string.Empty), target));
                Console.WriteLine($"Added '{args[1]}' ({cron})."); return 0;
            case "remove" when args.Length >= 2:
                store.Remove(args[1]); Console.WriteLine($"Removed '{args[1]}'."); return 0;
            case "pause":
                new KillSwitch().Paused = true; Console.WriteLine("All jobs paused (kill switch on)."); return 0;
            case "resume":
                new KillSwitch().Paused = false; Console.WriteLine("Jobs resumed."); return 0;
            case "install":
                var exe = Environment.ProcessPath ?? "ada";
                var ok = WindowsJobRunnerTask.Install($"\\\"{exe}\\\" run-due");
                Console.WriteLine(ok ? "Installed Windows task 'AdaJobRunner' (runs every 10 minutes)." : "Failed to install the Windows task.");
                return ok ? 0 : 1;
            case "uninstall":
                Console.WriteLine(WindowsJobRunnerTask.Uninstall() ? "Removed the Windows task." : "Nothing to remove (or failed).");
                return 0;
            default:
                Console.Error.WriteLine("usage: ada jobs [list | add | remove <name> | pause | resume | install | uninstall]");
                return 2;
        }
    }

    private static async Task<int> RunDue()
    {
        var kill = new KillSwitch();
        if (kill.Paused) { Console.WriteLine("Jobs are paused (kill switch on)."); return 0; }

        var audit = new FileAuditLog();
        using var provider = new ServiceCollection()
            .AddSingleton<IApprovalHandler>(new UnattendedApprovalHandler(new GrantStore().Load()))
            .AddSingleton<IAuditLog>(audit)
            .AddAda()
            .BuildServiceProvider();

        var engine = provider.GetRequiredService<IAdaEngine>();
        var count = await new JobRunner(new JobStore(), new NoteDeliveryService(), audit, kill).RunDueAsync(DateTime.UtcNow, engine);
        Console.WriteLine($"Ran {count} due job(s).");
        return 0;
    }

    private static int Skills(string[] args)
    {
        var sub = args.Length > 0 ? args[0].ToLowerInvariant() : "list";
        ISkill[] skills = [new ResearchSkill(new WebTools(new InMemoryAuditLog())), new DesktopSkill(), new FinanceRecordsSkill()];
        var registry = new SkillRegistry(skills);

        switch (sub)
        {
            case "list":
                foreach (var s in registry.Available)
                    Console.WriteLine($"  [{(registry.IsEnabled(s.Name) ? "x" : " ")}] {s.Name,-18} {(s.Mcp is not null ? "(mcp mount)" : string.Empty)}");
                return 0;
            case "enable" when args.Length >= 2:
                registry.Enable(args[1]); Console.WriteLine($"Enabled '{args[1]}'."); return 0;
            case "disable" when args.Length >= 2:
                registry.Disable(args[1]); Console.WriteLine($"Disabled '{args[1]}'."); return 0;
            default:
                Console.Error.WriteLine("usage: ada skills [list | enable <name> | disable <name>]"); return 2;
        }
    }

    private static async Task<int> Mcp(string[] args)
    {
        if (args.Length == 0)
        {
            Console.Error.WriteLine("usage: ada mcp <command> [args...]   e.g. ada mcp npx -y @modelcontextprotocol/server-everything");
            return 2;
        }

        var mount = new McpMount("cli", McpTransport.Stdio, Command: args[0], Args: args.Skip(1).ToArray());
        await using var mounter = new McpMounter(new AutoApprovalHandler(approveMutations: true), new InMemoryAuditLog());
        try
        {
            var tools = await mounter.MountAsync(mount);
            Console.WriteLine($"Mounted '{args[0]}' — {tools.Count} tool(s):");
            foreach (var t in tools.OfType<AIFunction>())
                Console.WriteLine($"  {t.Name}: {t.Description}");
            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"MCP mount failed: {ex.Message}");
            return 1;
        }
    }

    private static int Memory(string[] args)
    {
        var sub = args.Length > 0 ? args[0].ToLowerInvariant() : "list";
        using var store = new FileMemoryStore();

        switch (sub)
        {
            case "list":
                var all = store.List();
                if (all.Count == 0) { Console.WriteLine("No memories yet."); return 0; }
                foreach (var e in all) Console.WriteLine($"  {e.Name,-24} [{e.Type}] {e.Description}");
                return 0;

            case "recall":
                var hits = store.Recall(string.Join(' ', args.Skip(1)));
                if (hits.Count == 0) { Console.WriteLine("No matching memories."); return 0; }
                foreach (var h in hits) Console.WriteLine($"  {h.Name}: {h.Snippet}");
                return 0;

            case "remember":
                if (args.Length < 2) { Console.Error.WriteLine("usage: ada memory remember <name> --desc \"..\" --type reference --body \"..\""); return 2; }
                var flags = ParseFlags(args.Skip(2));
                var type = Enum.TryParse<MemoryType>(flags.GetValueOrDefault("type", "reference"), true, out var mt) ? mt : MemoryType.Reference;
                var entry = store.Remember(args[1], flags.GetValueOrDefault("desc", string.Empty), type, flags.GetValueOrDefault("body", string.Empty));
                Console.WriteLine($"Remembered '{entry.Name}'.");
                return 0;

            case "forget":
                if (args.Length < 2) { Console.Error.WriteLine("usage: ada memory forget <name>"); return 2; }
                Console.WriteLine(store.Forget(args[1]) ? $"Forgot '{args[1]}'." : $"No memory '{args[1]}'.");
                return 0;

            default:
                Console.Error.WriteLine("usage: ada memory [list | recall <query> | remember <name> --desc .. --type .. --body .. | forget <name>]");
                return 2;
        }
    }

    private static Dictionary<string, string> ParseFlags(IEnumerable<string> args)
    {
        var flags = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var a = args.ToArray();
        for (var i = 0; i < a.Length; i++)
            if (a[i].StartsWith("--", StringComparison.Ordinal) && i + 1 < a.Length) { flags[a[i][2..]] = a[i + 1]; i++; }
        return flags;
    }

    private static async Task Drain(IAsyncEnumerable<ChatResponseUpdate> stream)
    {
        await foreach (var _ in stream) { }
    }

    private static int Help()
    {
        Console.WriteLine("""
            ada — Ada Voice Agent CLI (test harness)

            usage:
              ada chat <message>          talk to the configured engine (echo, local, or hybrid)
              ada serve [--port N]        run the loopback web server (default: ephemeral port)
              ada selftest                run the cumulative headless acceptance checks
              ada auth list               list configured providers
              ada auth login <id> ...     add/update a provider (--key, --endpoint, --model, --kind, --role)
              ada auth logout <id>        remove a provider and its key
              ada providers               show the built-in provider catalog
              ada route <message>         show how a message would be routed (local vs escalation)
              ada memory list             list durable memories (also: recall, remember, forget)
              ada skills list             list skills (also: enable <name>, disable <name>)
              ada mcp <command> [args]    mount a stdio MCP server and list its tools
              ada jobs list               scheduled jobs (also: add, remove, pause, resume, install, uninstall)
              ada run-due                 run any due jobs now (what the Windows task invokes headless)
              ada config [profile|autostart]   show or set profile (Private/Balanced/Power) and autostart
              ada doctor                  print a readiness check
              ada model list|pull <id>|use <id>|status   manage the local ONNX model (Gemma/Phi)

            model config (env):
              ADA_PROVIDER=openai-compatible  ADA_ENDPOINT=http://localhost:11434/v1
              ADA_MODEL=qwen2.5:7b-instruct    ADA_API_KEY=(optional)   ADA_STAY_LOCAL=1
            """);
        return 0;
    }
}

using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Ada.Core;

namespace Ada.Server;

/// <summary>Request body for <c>POST /api/chat</c>. <paramref name="ThreadId"/> appends to an existing
/// conversation; when null/unknown, a new thread is created and its id is streamed back.</summary>
public sealed record ChatRequestDto(string Message, string? ThreadId = null);

/// <summary>Decision body for <c>POST /api/approvals/{id}</c>.</summary>
public sealed record ApprovalDecisionDto(bool Approved, bool Session = false);

/// <summary>Body for <c>POST /api/config</c> (all fields optional).</summary>
public sealed record UpdateConfigDto(string? Profile = null, bool? SetupComplete = null, bool? Autostart = null);

/// <summary>Body for <c>POST /api/providers</c> — connect a provider from the setup wizard.</summary>
public sealed record AddProviderDto(string Id, string? Key = null, string? Endpoint = null, string? Model = null);

/// <summary>Body for <c>POST /api/sandbox</c> — the AIO sandbox preference and the image-prefetch toggle.</summary>
public sealed record SandboxToggleDto(bool? Enabled = null, bool? Prefetch = null);

/// <summary>
/// Maps Ada's loopback HTTP surface: the chat UI at <c>/</c>, a health probe, the streaming
/// <c>/api/chat</c> endpoint, and the approval channel (<c>/api/approvals/stream</c> +
/// <c>POST /api/approvals/{id}</c>) that drives the WebView2 approval cards. The UI is an embedded
/// resource so the server is self-contained.
/// </summary>
public static class AdaApi
{
    private static readonly string IndexHtml = LoadHtml("index.html");
    private static readonly string VoiceHtml = LoadHtml("voice.html");

    public static void Map(WebApplication app)
    {
        app.MapGet("/", () => Results.Content(IndexHtml, "text/html; charset=utf-8"));
        app.MapGet("/voiceui", () => Results.Content(VoiceHtml, "text/html; charset=utf-8")); // the compact Voice Mode widget
        app.MapGet("/healthz", () => Results.Json(new { status = "ok", app = "ada", milestone = "M2" }));

        // Launch readiness for the startup splash: is a brain reachable (the gating check), are the speech
        // models cached? Kicks an observable, download-free Ollama start when it's the configured-but-idle
        // runtime, so the page shows "starting → ready" instead of the first turn hanging on "thinking".
        app.MapGet("/api/launch", async (HttpContext http, CancellationToken ct) =>
        {
            var r = await LaunchStatus.BuildAsync(http.RequestServices, ct);
            return Results.Json(new
            {
                ready = r.Ready,
                setupComplete = r.SetupComplete,
                stage = r.Stage,
                checks = r.Checks.Select(c => new { name = c.Name, status = c.Status, detail = c.Detail, action = c.Action }),
            });
        });

        app.MapPost("/api/chat", async (ChatRequestDto dto, IAdaEngine engine, IConversationStore convos, HttpContext http, CancellationToken ct) =>
        {
            var resp = http.Response;
            resp.Headers.ContentType = "text/event-stream";
            resp.Headers.CacheControl = "no-cache";

            var message = dto.Message ?? string.Empty;
            // Resolve (or create) the thread this turn belongs to, and tell the UI which one it is.
            var threadId = dto.ThreadId;
            if (string.IsNullOrEmpty(threadId) || convos.Load(threadId) is null)
            {
                var convo = convos.Create(message);
                threadId = convo.Id;
                await WriteSse(resp, "thread", JsonSerializer.Serialize(new { id = convo.Id, title = convo.Title }), ct);
            }

            await foreach (var chunk in engine.RespondAsync(new AdaRequest(message, threadId), ct))
            {
                if (chunk.IsFinal)
                    await WriteSse(resp, "done", JsonSerializer.Serialize(new { route = chunk.Route, threadId }), ct);
                else
                    await WriteSse(resp, "chunk", JsonSerializer.Serialize(new { text = chunk.Text }), ct);
            }
        });

        // Conversation threads (durable history) — one JSON file each under %APPDATA%\Ada\conversations.
        app.MapGet("/api/threads", (IConversationStore convos) => Results.Json(convos.List()));
        app.MapGet("/api/threads/{id}", (string id, IConversationStore convos) =>
            convos.Load(id) is { } c ? Results.Json(c) : Results.NotFound());
        app.MapDelete("/api/threads/{id}", (string id, IConversationStore convos) =>
            convos.Delete(id) ? Results.Ok() : Results.NotFound());

        // Approval cards: the agent's gated tools surface here; the UI renders a card and POSTs back.
        app.MapGet("/api/approvals/stream", async (IApprovalHandler approvals, HttpContext http, CancellationToken ct) =>
        {
            if (approvals is not InteractiveApprovalHandler handler) return;
            var resp = http.Response;
            resp.Headers.ContentType = "text/event-stream";
            resp.Headers.CacheControl = "no-cache";

            var channel = System.Threading.Channels.Channel.CreateUnbounded<ApprovalRequest>();
            void OnRequested(ApprovalRequest r) => channel.Writer.TryWrite(r);
            handler.Requested += OnRequested;
            try
            {
                foreach (var pending in handler.Pending_)
                    await WriteSse(resp, "approval", Serialize(pending), ct);

                await foreach (var request in channel.Reader.ReadAllAsync(ct))
                    await WriteSse(resp, "approval", Serialize(request), ct);
            }
            catch (OperationCanceledException) { /* client went away */ }
            finally { handler.Requested -= OnRequested; }
        });

        app.MapPost("/api/approvals/{id}", (string id, ApprovalDecisionDto dto, IApprovalHandler approvals) =>
        {
            if (approvals is not InteractiveApprovalHandler handler) return Results.NotFound();
            var decision = dto.Approved
                ? ApprovalDecision.Approve(dto.Session ? ApprovalGrant.Session : ApprovalGrant.Once)
                : ApprovalDecision.Denied;
            return handler.Resolve(id, decision) ? Results.Ok() : Results.NotFound();
        });

        // Settings + setup wizard (spec §11.3, §15) — so the user never touches a terminal or JSON.
        app.MapGet("/api/config", (ConfigStore configStore, ProviderRegistry providers, SkillRegistry? skills) =>
        {
            var c = configStore.Load();
            return Results.Json(new
            {
                profile = c.Profile.ToString(),
                setupComplete = c.SetupComplete,
                autostart = c.Autostart,
                hotkey = c.Hotkey,
                catalog = ProviderCatalog.BuiltIns.Select(e => new { e.Id, e.Label, auth = e.Auth.ToString() }),
                providers = providers.Configured.Select(p => new { p.Id, kind = p.Kind.ToString(), role = p.Role.ToString(), p.ModelId }),
                skills = (skills?.Available ?? []).Select(s => new { s.Name, enabled = skills!.IsEnabled(s.Name), mcp = s.Mcp is not null }),
            });
        });

        app.MapPost("/api/config", (UpdateConfigDto dto, ConfigStore configStore) =>
        {
            var c = configStore.Load();
            if (dto.Profile is not null && Enum.TryParse<AdaProfile>(dto.Profile, true, out var p)) c.Profile = p;
            if (dto.SetupComplete is { } done) c.SetupComplete = done;
            if (dto.Autostart is { } auto) c.Autostart = auto;
            configStore.Save(c);
            return Results.Ok();
        });

        app.MapPost("/api/providers", (AddProviderDto dto, ProviderStore store, ICredentialVault vault) =>
        {
            var catalog = ProviderCatalog.Find(dto.Id);
            var kind = catalog?.Kind ?? ProviderKind.OpenAiCompatible;
            var auth = !string.IsNullOrEmpty(dto.Key) ? AuthMethod.ApiKey : catalog?.Auth ?? AuthMethod.None;
            var role = catalog?.Auth == AuthMethod.None ? ModelRole.Default : ModelRole.Escalation;
            var config = new ProviderConfig(dto.Id, kind, dto.Model ?? catalog?.DefaultModel ?? "model", dto.Endpoint ?? catalog?.DefaultEndpoint, auth, role);
            store.Upsert(config);
            if (!string.IsNullOrEmpty(dto.Key)) vault.Set(config.VaultKey, dto.Key);
            return Results.Ok();
        });

        app.MapGet("/api/jobs", (JobStore store) =>
            Results.Json(store.Load().Select(j => new { j.Name, j.Cron, delivery = j.Delivery.ToString(), j.Enabled })));

        // File-based skills (MAF) discovered under %APPDATA%\Ada\skills. Drives Settings → Skills.
        app.MapGet("/api/skills", (SandboxSession session) => Results.Json(new
        {
            dir = AdaPaths.SkillsDir,
            sandboxActive = session.Active,   // bundled scripts run only inside the sandbox
            skills = AdaSkills.List().Select(s => new { s.Name, s.Description, s.Compatibility, s.HasScripts }),
        }));

        // Open the skills folder in Explorer so the user can drop a skill in. Local + same machine.
        app.MapPost("/api/skills/open", () =>
        {
            try { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(AdaPaths.EnsureSkillsDir()) { UseShellExecute = true }); }
            catch { /* best effort */ }
            return Results.Ok();
        });

        // Install a skill from an uploaded .zip / .skill archive — the request body is the raw bytes.
        // Validated then extracted into the skills folder; scripts never run here, only files are written.
        app.MapPost("/api/skills/upload", async (HttpContext http, CancellationToken ct) =>
        {
            try
            {
                using var ms = new MemoryStream();
                await http.Request.Body.CopyToAsync(ms, ct);
                if (ms.Length == 0) return Results.Json(new { ok = false, error = "No file was received." });
                ms.Position = 0;
                var result = SkillInstaller.InstallFromZip(ms);
                return Results.Json(new { ok = result.Ok, name = result.Name, error = result.Error });
            }
            catch (Exception ex)
            {
                return Results.Json(new { ok = false, error = ex.Message });
            }
        });

        // The work environment: whether the AIO sandbox is up (and the agent is using its tools) or on
        // the host fallback. Drives Settings → Workspace & sandbox.
        app.MapGet("/api/sandbox", (SandboxSession session, ConfigStore config) =>
        {
            var c = config.Load();
            return Results.Json(new
            {
                enabled = c.SandboxEnabled,
                prefetch = c.PrefetchImages,
                active = session.Active,
                mode = session.Mode,
                endpoint = session.Endpoint,
                workspace = session.Workspace,
                toolCount = session.Tools.Count,
            });
        });

        // Turn the sandbox preference (and image prefetch) on/off. Persisted; the running agent's tools are
        // fixed once it has started replying, so a change applies fully on Ada's next launch.
        app.MapPost("/api/sandbox", (SandboxToggleDto dto, ConfigStore config) =>
        {
            var c = config.Load();
            if (dto.Enabled is { } e) c.SandboxEnabled = e;
            if (dto.Prefetch is { } p) c.PrefetchImages = p;
            config.Save(c);
            return Results.Json(new { enabled = c.SandboxEnabled, prefetch = c.PrefetchImages });
        });

        // Set up the sandbox from Settings: pull the image (first run is large) + start the container with
        // the workspace mounted, then mount its /mcp tools — streamed as SSE progress, mirroring Ollama setup.
        app.MapPost("/api/sandbox/setup", async (HttpContext http, SandboxSession session, McpMounter mounter, ImageProvisioner prov, CancellationToken ct) =>
        {
            http.Response.Headers.ContentType = "text/event-stream";
            IProgress<string> progress = new Progress<string>(s =>
                _ = http.Response.WriteAsync($"event: progress\ndata: {JsonSerializer.Serialize(s)}\n\n", ct));
            try
            {
                if (session.Active) { await http.Response.WriteAsync("event: done\ndata: {}\n\n", ct); return; } // already up

                var runtime = await AioSandboxRuntime.StartAsync(new AioSandboxOptions(), allowPull: true, progress, ct);
                if (runtime is null)
                {
                    await http.Response.WriteAsync("event: error\ndata: \"Couldn't start the sandbox. Is Docker Desktop installed and running?\"\n\n", ct);
                    return;
                }

                progress.Report("Connecting Ada's tools…");
                var mount = new McpMount("sandbox", McpTransport.Http, Url: runtime.McpUrl, IsEgress: false, GateMutatingTools: false);
                var tools = await mounter.MountAsync(mount, ct);
                session.Activate(runtime.Endpoint, tools);

                var store = new ConfigStore();
                var c = store.Load(); c.SandboxEnabled = true; store.Save(c);

                // While we're online and the user has just opted in, pre-pull the run_code runtimes too so the
                // first code run is instant. Best-effort — the sandbox is already usable without them.
                progress.Report("Getting the code runtimes ready…");
                await prov.PrefetchMissingAsync(progress, includeCore: false, ct);

                await http.Response.WriteAsync("event: done\ndata: {}\n\n", ct);
            }
            catch (Exception ex)
            {
                await http.Response.WriteAsync($"event: error\ndata: {JsonSerializer.Serialize(ex.Message)}\n\n", ct);
            }
        });

        // The Docker images Ada manages (the sandbox + the run_code runtimes): what's on disk and how big.
        // Drives the image list in Settings → Workspace & sandbox.
        app.MapGet("/api/images", async (ImageProvisioner prov, CancellationToken ct) =>
        {
            var s = await prov.StatusAsync(ct);
            return Results.Json(new
            {
                dockerAvailable = s.DockerAvailable,
                images = s.Images.Select(i => new { i.Key, i.Title, i.Purpose, i.Reference, i.Core, i.Present, sizeText = i.SizeText }),
            });
        });

        // Download Ada's images with streamed progress so the user never has to run `docker pull`. ?key=<id>
        // pulls one; ?key=all includes the big AIO image; no key tops up the missing run_code runtimes.
        app.MapPost("/api/images/pull", async (HttpContext http, ImageProvisioner prov, CancellationToken ct) =>
        {
            http.Response.Headers.ContentType = "text/event-stream";
            IProgress<string> progress = new Progress<string>(s =>
                _ = http.Response.WriteAsync($"event: progress\ndata: {JsonSerializer.Serialize(s)}\n\n", ct));
            try
            {
                if (!await prov.DockerAvailableAsync(ct))
                {
                    await http.Response.WriteAsync("event: error\ndata: \"Docker isn't available. Install or start Docker Desktop, then try again.\"\n\n", ct);
                    return;
                }

                var key = http.Request.Query["key"].FirstOrDefault();
                if (string.IsNullOrEmpty(key) || key == "all")
                    await prov.PrefetchMissingAsync(progress, includeCore: key == "all", ct);
                else if (ImageProvisioner.Find(key) is { } img)
                    await prov.PullAsync(img, progress, ct);
                else { await http.Response.WriteAsync("event: error\ndata: \"Unknown image.\"\n\n", ct); return; }

                await http.Response.WriteAsync("event: done\ndata: {}\n\n", ct);
            }
            catch (Exception ex)
            {
                await http.Response.WriteAsync($"event: error\ndata: {JsonSerializer.Serialize(ex.Message)}\n\n", ct);
            }
        });

        // Which Ollama models are already pulled (model:tag), so the picker can mark downloaded vs not.
        app.MapGet("/api/ollama/models", () => Results.Json(new { models = OllamaRuntime.InstalledModels() }));

        // Set up the managed Ollama runtime from the wizard: detect-or-download, pull the model, and
        // make it the local runtime — streamed as SSE progress. Ollama is left running for the session.
        app.MapPost("/api/ollama/setup", async (HttpContext http, CancellationToken ct) =>
        {
            var model = http.Request.Query["model"].FirstOrDefault() ?? new OllamaOptions().DefaultModel;
            http.Response.Headers.ContentType = "text/event-stream";
            var progress = new Progress<string>(s =>
                _ = http.Response.WriteAsync($"event: progress\ndata: {JsonSerializer.Serialize(s)}\n\n", ct));
            try
            {
                var runtime = await OllamaRuntime.StartAsync(new OllamaOptions(), allowDownload: true, progress, ct);
                if (runtime is null) { await http.Response.WriteAsync("event: error\ndata: \"Could not start Ollama.\"\n\n", ct); return; }

                await runtime.PullAsync(model, progress, ct); // intentionally left running for this session
                var cfg = new ConfigStore();
                var c = cfg.Load(); c.LocalRuntime = "ollama"; c.OllamaModel = model; cfg.Save(c);
                await http.Response.WriteAsync("event: done\ndata: {}\n\n", ct);
            }
            catch (Exception ex)
            {
                await http.Response.WriteAsync($"event: error\ndata: {JsonSerializer.Serialize(ex.Message)}\n\n", ct);
            }
        });

        // Local ONNX models — the catalog + which are downloaded (the wizard's "download a brain" step).
        app.MapGet("/api/models", () =>
        {
            var store = new OnnxModelStore();
            var active = new ConfigStore().Load().LocalModelId;
            return Results.Json(new
            {
                active,
                models = OnnxModelCatalog.Models.Select(m => new
                {
                    m.Id, m.Label, m.ApproxMb, m.License, downloaded = store.IsReady(m.Id),
                }),
            });
        });

        app.MapPost("/api/models/{id}/pull", async (string id, HttpContext http, CancellationToken ct) =>
        {
            var entry = OnnxModelCatalog.Find(id);
            if (entry is null) { http.Response.StatusCode = 404; return; }

            http.Response.Headers.ContentType = "text/event-stream";
            var store = new OnnxModelStore();
            var progress = new Progress<DownloadProgress>(p =>
                _ = http.Response.WriteAsync($"event: progress\ndata: {{\"file\":\"{p.File}\",\"i\":{p.FileIndex},\"n\":{p.FileCount}}}\n\n", ct));
            try
            {
                await store.DownloadAsync(entry, progress, ct);
                var cfg = new ConfigStore();
                var c = cfg.Load(); c.LocalModelId = entry.Id; cfg.Save(c);
                await http.Response.WriteAsync("event: done\ndata: {}\n\n", ct);
            }
            catch (Exception ex)
            {
                await http.Response.WriteAsync($"event: error\ndata: {System.Text.Json.JsonSerializer.Serialize(ex.Message)}\n\n", ct);
            }
        });
    }

    private static string Serialize(ApprovalRequest r) => JsonSerializer.Serialize(new
    {
        id = r.Id,
        tool = r.Tool,
        tier = r.Tier.ToString(),
        summary = r.Summary,
        detail = r.Detail,
    });

    private static async Task WriteSse(HttpResponse resp, string ev, string jsonData, CancellationToken ct)
    {
        await resp.WriteAsync($"event: {ev}\ndata: {jsonData}\n\n", ct);
        await resp.Body.FlushAsync(ct);
    }

    private static string LoadHtml(string suffix)
    {
        var asm = typeof(AdaApi).Assembly;
        var name = asm.GetManifestResourceNames()
            .First(n => n.EndsWith(suffix, StringComparison.OrdinalIgnoreCase));
        using var stream = asm.GetManifestResourceStream(name)!;
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }
}

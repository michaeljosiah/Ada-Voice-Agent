using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Ada.Core;

namespace Ada.Server;

/// <summary>Request body for <c>POST /api/chat</c>.</summary>
public sealed record ChatRequestDto(string Message);

/// <summary>Decision body for <c>POST /api/approvals/{id}</c>.</summary>
public sealed record ApprovalDecisionDto(bool Approved, bool Session = false);

/// <summary>Body for <c>POST /api/config</c> (all fields optional).</summary>
public sealed record UpdateConfigDto(string? Profile = null, bool? SetupComplete = null, bool? Autostart = null);

/// <summary>Body for <c>POST /api/providers</c> — connect a provider from the setup wizard.</summary>
public sealed record AddProviderDto(string Id, string? Key = null, string? Endpoint = null, string? Model = null);

/// <summary>
/// Maps Ada's loopback HTTP surface: the chat UI at <c>/</c>, a health probe, the streaming
/// <c>/api/chat</c> endpoint, and the approval channel (<c>/api/approvals/stream</c> +
/// <c>POST /api/approvals/{id}</c>) that drives the WebView2 approval cards. The UI is an embedded
/// resource so the server is self-contained.
/// </summary>
public static class AdaApi
{
    private static readonly string IndexHtml = LoadIndexHtml();

    public static void Map(WebApplication app)
    {
        app.MapGet("/", () => Results.Content(IndexHtml, "text/html; charset=utf-8"));
        app.MapGet("/healthz", () => Results.Json(new { status = "ok", app = "ada", milestone = "M2" }));

        app.MapPost("/api/chat", async (ChatRequestDto dto, IAdaEngine engine, HttpContext http, CancellationToken ct) =>
        {
            var resp = http.Response;
            resp.Headers.ContentType = "text/event-stream";
            resp.Headers.CacheControl = "no-cache";

            await foreach (var chunk in engine.RespondAsync(new AdaRequest(dto.Message ?? string.Empty), ct))
            {
                if (chunk.IsFinal)
                    await WriteSse(resp, "done", JsonSerializer.Serialize(new { route = chunk.Route }), ct);
                else
                    await WriteSse(resp, "chunk", JsonSerializer.Serialize(new { text = chunk.Text }), ct);
            }
        });

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

    private static string LoadIndexHtml()
    {
        var asm = typeof(AdaApi).Assembly;
        var name = asm.GetManifestResourceNames()
            .First(n => n.EndsWith("index.html", StringComparison.OrdinalIgnoreCase));
        using var stream = asm.GetManifestResourceStream(name)!;
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }
}

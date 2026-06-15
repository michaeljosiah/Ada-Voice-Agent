using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Ada.Core;

namespace Ada.Server;

/// <summary>Request body for <c>POST /api/chat</c>.</summary>
public sealed record ChatRequestDto(string Message);

/// <summary>Decision body for <c>POST /api/approvals/{id}</c>.</summary>
public sealed record ApprovalDecisionDto(bool Approved, bool Session = false);

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

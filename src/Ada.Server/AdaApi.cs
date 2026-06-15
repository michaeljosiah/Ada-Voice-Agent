using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Ada.Core;

namespace Ada.Server;

/// <summary>Request body for <c>POST /api/chat</c>.</summary>
public sealed record ChatRequestDto(string Message);

/// <summary>
/// Maps Ada's loopback HTTP surface: the chat UI at <c>/</c>, a health probe, and the streaming
/// <c>/api/chat</c> endpoint (Server-Sent Events). The UI is shipped as an embedded resource so the
/// server is self-contained and there are no loose files to deploy next to the host exe.
/// </summary>
public static class AdaApi
{
    private static readonly string IndexHtml = LoadIndexHtml();

    public static void Map(WebApplication app)
    {
        app.MapGet("/", () => Results.Content(IndexHtml, "text/html; charset=utf-8"));
        app.MapGet("/healthz", () => Results.Json(new { status = "ok", app = "ada", milestone = "M0" }));

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
    }

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

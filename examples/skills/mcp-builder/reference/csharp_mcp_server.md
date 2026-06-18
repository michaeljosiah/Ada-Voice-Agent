# C# / .NET MCP Server Guide

Build an MCP server in C# with the official **`ModelContextProtocol`** SDK. This is the natural choice when the server will run alongside Ada (a .NET 10 app) or you simply prefer C#. The design rules in `mcp_best_practices.md` apply unchanged — this file only covers the C#-specific mechanics.

> The C# SDK is pre-1.0; verify exact type/method names against the current source at `https://github.com/modelcontextprotocol/csharp-sdk` (and its README) before relying on them.

## Project setup

```bash
dotnet new console -n MyMcpServer
cd MyMcpServer
dotnet add package ModelContextProtocol --prerelease
dotnet add package Microsoft.Extensions.Hosting
```

For a remote (Streamable HTTP) server instead of stdio, also add:

```bash
dotnet add package ModelContextProtocol.AspNetCore --prerelease
```

## Minimal stdio server (local servers)

`Program.cs`:

```csharp
using Microsoft.Extensions.Hosting;
using ModelContextProtocol.Server;
using System.ComponentModel;

var builder = Host.CreateApplicationBuilder(args);

// Log to stderr — stdout is the MCP transport and must carry only protocol messages.
builder.Logging.AddConsole(o => o.LogToStandardErrorThreshold = LogLevel.Trace);

builder.Services
    .AddMcpServer()
    .WithStdioServerTransport()
    .WithToolsFromAssembly();   // discovers [McpServerTool] methods in this assembly

await builder.Build().RunAsync();

[McpServerToolType]
public static class WeatherTools
{
    [McpServerTool, Description("Gets the current temperature for a city, in Celsius.")]
    public static async Task<string> GetTemperature(
        HttpClient http,                                  // injected from DI
        [Description("City name, e.g. \"London\"")] string city)
    {
        // ... call your upstream API with `http`, return a focused, agent-friendly string ...
        return $"It is 18°C in {city}.";
    }

    [McpServerTool, Description("Adds two integers and returns the sum.")]
    public static int Add(int a, int b) => a + b;
}
```

Run with `dotnet run`. The server speaks MCP over stdin/stdout.

Key points:
- **stdout is sacred.** Never `Console.WriteLine` diagnostics — route logs to stderr (as above) or you will corrupt the protocol stream.
- Tools are `[McpServerTool]` methods inside a `[McpServerToolType]` class. Parameters become the input schema; put a `[Description]` on the method and every parameter. Services (e.g. `HttpClient`) are injected from DI.
- Return `Task<T>`/`ValueTask<T>` for I/O-bound tools. Return a string (or a typed object for structured content) describing the result — keep it concise and paginated.

## Remote server (Streamable HTTP)

```csharp
using ModelContextProtocol.Server;

var builder = WebApplication.CreateBuilder(args);
builder.Services.AddMcpServer().WithHttpTransport().WithToolsFromAssembly();

var app = builder.Build();
app.MapMcp();        // exposes the MCP endpoint (Streamable HTTP)
app.Run();
```

Prefer stateless JSON over Streamable HTTP for servers you intend to scale (simpler than stateful sessions), per `mcp_best_practices.md`.

## Tool annotations

Express the standard hints via the attribute so clients can reason about safety:

```csharp
[McpServerTool(
    Name = "delete_record",
    ReadOnly = false,
    Destructive = true,
    Idempotent = true,
    OpenWorld = true)]
[Description("Deletes a record by id. Returns a confirmation message.")]
public static string DeleteRecord(string id) => /* ... */ $"Deleted {id}.";
```

## Mounting the finished server in Ada

- **stdio**: point Ada's MCP mounter at the launch command — `dotnet run` in the project directory, or the published executable.
- **HTTP**: give Ada the server's base URL (e.g. `http://127.0.0.1:<port>`); Ada's mounter auto-detects Streamable HTTP / SSE.

Write-capable tools mounted into Ada are gated by Ada's approval flow unless the server runs inside Ada's sandbox zone (where the zone boundary is the gate). Keep secrets/credentials on the server side — expose them only through tools, never as raw values in tool output.

## Build & test

- `dotnet build` to verify compilation.
- Inspect interactively with the MCP Inspector: `npx @modelcontextprotocol/inspector dotnet run`.
- Then proceed to **Phase 4** in the main guide and write 10 evaluation questions for your tools.

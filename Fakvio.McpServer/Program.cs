using Fakvio.McpServer.Client;
using Fakvio.McpServer.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

// ──────────────────────────────────────────────────────────────────────
// Fakvio MCP Server — Entry Point
//
// This is a console application that speaks the Model Context Protocol
// (MCP) over stdio. AI clients like Claude Code connect to it and can
// invoke invoice, client, template, and reporting tools.
//
// Architecture:
//   AI Client ←stdio→ this process ←HTTP/JWT→ Fakvio.API ←EF Core→ DB
//
// Required environment variables:
//   FAKVIO_API_TOKEN  — JWT bearer token for API authentication
//   FAKVIO_API_URL    — (optional) API base URL, defaults to https://localhost:7001
// ──────────────────────────────────────────────────────────────────────

var builder = Host.CreateApplicationBuilder(args);

// ── Logging ────────────────────────────────────────────────────────
// MCP protocol uses stdout for JSON-RPC messages, so ALL logging
// must go to stderr. Otherwise log lines corrupt the protocol stream.
builder.Logging.AddConsole(options =>
{
    options.LogToStandardErrorThreshold = LogLevel.Trace;
});

// ── Configuration ──────────────────────────────────────────────────
// Read API URL and token from environment variables.
// FAKVIO_API_TOKEN is required — fail fast if missing.
var settings = new McpServerSettings
{
    ApiBaseUrl = Environment.GetEnvironmentVariable("FAKVIO_API_URL") ?? "https://localhost:7001",
    ApiToken = Environment.GetEnvironmentVariable("FAKVIO_API_TOKEN") ?? string.Empty
};

if (string.IsNullOrWhiteSpace(settings.ApiToken))
{
    Console.Error.WriteLine("ERROR: FAKVIO_API_TOKEN environment variable is required.");
    Console.Error.WriteLine("Set it to a valid JWT token obtained from the Fakvio API login endpoint.");
    return 1;
}

// Register settings as a singleton so tools/services can inject it
builder.Services.AddSingleton(settings);

// ── Outbound authentication ────────────────────────────────────────
// The bearer token is resolved per request by AuthHeaderHandler, never baked
// into HttpClient.DefaultRequestHeaders. Defaults are shared by every call on
// that client, so a token stored there would be sent on behalf of whoever comes
// later — harmless in stdio (one process = one user), a cross-tenant leak once
// the same server is hosted over HTTP.
//
// In stdio mode the credential is the FAKVIO_API_TOKEN env var; the HTTP
// transport will swap in a session-scoped provider behind the same interface.
builder.Services.AddSingleton<IApiTokenProvider, EnvironmentApiTokenProvider>();
builder.Services.AddTransient<AuthHeaderHandler>();

// ── HTTP Client ────────────────────────────────────────────────────
// Register a typed HttpClient for IFakvioApiClient → FakvioApiClient.
// The factory configures the base address; AuthHeaderHandler adds the
// Authorization header to each individual request.
builder.Services.AddHttpClient<IFakvioApiClient, FakvioApiClient>(client =>
{
    client.BaseAddress = new Uri(settings.ApiBaseUrl.TrimEnd('/') + "/");
    client.DefaultRequestHeaders.Accept.Add(
        new System.Net.Http.Headers.MediaTypeWithQualityHeaderValue("application/json"));
})
.AddHttpMessageHandler<AuthHeaderHandler>();

// ── MCP Server ─────────────────────────────────────────────────────
// Register the MCP server with stdio transport (for CLI integration).
// WithToolsFromAssembly() discovers all [McpServerToolType] classes
// and registers their [McpServerTool] methods as available tools.
builder.Services
    .AddMcpServer(options =>
    {
        options.ServerInfo = new()
        {
            Name = "fakvio",
            Version = "1.0.0"
        };
    })
    .WithStdioServerTransport()
    .WithToolsFromAssembly();

// ── Run ────────────────────────────────────────────────────────────
await builder.Build().RunAsync();
return 0;

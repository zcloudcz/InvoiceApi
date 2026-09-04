using Fakvio.McpServer;
using Fakvio.McpServer.Client;
using Fakvio.McpServer.Configuration;
using Fakvio.McpServer.Http;
using Fakvio.McpServer.Tools;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

// ──────────────────────────────────────────────────────────────────────
// Fakvio MCP Server — Entry Point
//
// One server, two hosting modes, the same 37 tools (same assembly, same
// WithToolsFromAssembly() scan — see McpServerRegistration):
//
//   stdio (default)  AI client ←stdio→ this process ←HTTP/JWT→ Fakvio.API ←EF Core→ DB
//   http             AI clients ←HTTP/MCP→ this process ←HTTP/API key→ Fakvio.API ←EF Core→ DB
//
// The modes differ in exactly one thing that matters: where the API credential
// comes from. stdio serves a single local user, so the process credential IS the
// user's credential (FAKVIO_API_TOKEN). HTTP serves many users, so the credential
// must come from the caller's own request and never from a value captured at
// startup — see IApiTokenProvider and HttpContextApiTokenProvider.
//
// Environment variables:
//   FAKVIO_MCP_TRANSPORT — "stdio" (default) or "http"
//   FAKVIO_API_URL       — API base URL, defaults to https://localhost:7047
//   FAKVIO_API_TOKEN     — JWT bearer token; REQUIRED in stdio mode, unused in http mode
//   ASPNETCORE_URLS      — http mode only: what Kestrel binds to (standard ASP.NET Core)
// ──────────────────────────────────────────────────────────────────────

// ── Configuration ──────────────────────────────────────────────────
var settings = McpServerSettings.FromEnvironment();

// Fail fast on a misspelled transport instead of silently falling back to stdio — a server
// that was meant to be reachable over HTTP and instead sits waiting on stdin looks "started"
// to everything watching it.
var rawTransport = Environment.GetEnvironmentVariable("FAKVIO_MCP_TRANSPORT");
var transport = EMcpTransport.Stdio;

if (!string.IsNullOrWhiteSpace(rawTransport)
    && !Enum.TryParse(rawTransport, ignoreCase: true, out transport))
{
    Console.Error.WriteLine($"ERROR: FAKVIO_MCP_TRANSPORT='{rawTransport}' is not a known transport.");
    Console.Error.WriteLine("Use 'stdio' (default) or 'http'.");
    return 1;
}

if (transport == EMcpTransport.Http)
{
    // ── HTTP mode ──────────────────────────────────────────────────
    // Credentials arrive per request; there is nothing to check at startup.
    var webBuilder = WebApplication.CreateBuilder(args);

    McpHttpHost.ConfigureServices(webBuilder.Services, settings);

    var app = webBuilder.Build();
    McpHttpHost.MapEndpoints(app);

    // Wire the shared logger used by every MCP tool's catch-all error handler
    // (McpToolError, issue #279). Tool methods are static, so this is set once
    // per host here instead of adding an ILogger parameter to every tool signature.
    McpToolError.Logger = app.Services.GetRequiredService<ILoggerFactory>()
        .CreateLogger("Fakvio.McpServer.Tools");

    await app.RunAsync();
    return 0;
}

// ── stdio mode ─────────────────────────────────────────────────────
// FAKVIO_API_TOKEN is the only credential this mode has — fail fast if missing.
if (string.IsNullOrWhiteSpace(settings.ApiToken))
{
    Console.Error.WriteLine("ERROR: FAKVIO_API_TOKEN environment variable is required in stdio mode.");
    Console.Error.WriteLine("Set it to a valid JWT token obtained from the Fakvio API login endpoint.");
    return 1;
}

var builder = Host.CreateApplicationBuilder(args);

// ── Logging ────────────────────────────────────────────────────────
// MCP over stdio uses stdout for JSON-RPC messages, so ALL logging must go to
// stderr. Otherwise log lines corrupt the protocol stream.
builder.Logging.AddConsole(options =>
{
    options.LogToStandardErrorThreshold = LogLevel.Trace;
});

// One process serves exactly one user here, so "the process credential" and "the caller's
// credential" are the same thing. It is still a singleton resolved per request through
// IApiTokenProvider — see that interface for why the registration rule has no exceptions.
builder.Services.AddSingleton<IApiTokenProvider, EnvironmentApiTokenProvider>();

builder.Services
    .AddFakvioMcpServer(settings)
    .WithStdioServerTransport();

var host = builder.Build();

// Wire the shared logger used by every MCP tool's catch-all error handler
// (McpToolError, issue #279). Tool methods are static, so this is set once
// per host here instead of adding an ILogger parameter to every tool signature.
McpToolError.Logger = host.Services.GetRequiredService<ILoggerFactory>()
    .CreateLogger("Fakvio.McpServer.Tools");

await host.RunAsync();
return 0;

using Fakvio.McpServer.Client;
using Fakvio.McpServer.Configuration;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using ModelContextProtocol.AspNetCore;

namespace Fakvio.McpServer.Http;

/// <summary>
/// Composition of the HTTP hosting mode: MCP Streamable HTTP on <c>/mcp</c>, behind the
/// API-key gate, with the caller's own credential forwarded to the Fakvio API.
///
/// <para>
/// Split into two methods (services / endpoints) instead of building the whole
/// <c>WebApplication</c> here so the tests can run this exact configuration on an in-memory
/// test server. Everything security-relevant lives in these two methods, and both the real
/// host and the tests go through them.
/// </para>
/// </summary>
public static class McpHttpHost
{
    /// <summary>Route prefix the MCP Streamable HTTP transport is mapped to.</summary>
    public const string EndpointPath = "/mcp";

    /// <summary>
    /// Registers the HTTP-mode services: the ambient-context token provider plus everything
    /// the two transports share, wired to the Streamable HTTP transport.
    /// </summary>
    public static void ConfigureServices(IServiceCollection services, McpServerSettings settings)
    {
        // IHttpContextAccessor is what makes "the caller's token" reachable from inside the
        // outbound HTTP pipeline. It is a singleton over AsyncLocal — see
        // HttpContextApiTokenProvider for why that, and not AddScoped, is the safe shape.
        services.AddHttpContextAccessor();
        services.AddSingleton<IApiTokenProvider, HttpContextApiTokenProvider>();

        services.AddFakvioMcpServer(settings)
            .WithHttpTransport(options =>
            {
                // Written out rather than left to the SDK default, because the credential
                // design depends on it. Reading the caller's token off HttpContext only works
                // while the tool handler runs on the execution context of the HTTP request
                // that carried the call. Stateless guarantees that: every request builds a
                // fresh server context and the handler runs inside that request.
                //
                // A stateful session happens to behave the same today — but only because
                // PerSessionExecutionContext defaults to false. Flip that one and every tool
                // call runs on the initialize request's context instead, HttpContext is null
                // for the caller, and the API answers 401 (covered by
                // McpHttpTransportTests). Pinning stateless means the design rests on one
                // stated choice instead of on two defaults nobody is watching.
                //
                // It also means no session affinity is required, so the host can be scaled
                // out without sticky routing (relevant for #241).
                options.SessionMode = HttpServerSessionMode.Stateless;
            });
    }

    /// <summary>
    /// Puts the API-key gate in front of everything and maps the MCP endpoints.
    /// Order is the security boundary: the gate is registered before the endpoints, so no
    /// request reaches the transport without a credential the API has just accepted.
    /// </summary>
    public static void MapEndpoints(WebApplication app)
    {
        app.Use(McpApiKeyMiddleware.InvokeAsync);
        app.MapMcp(EndpointPath);
    }
}

using Fakvio.Application.Service;
using Fakvio.Infrastructure.Authentication.OAuth;
using Fakvio.Infrastructure.Service;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Fakvio.Infrastructure.DependencyInjection;

/// <summary>
/// DI registration for MCP OAuth 2.1 (ADR 0001, docs/adr/0001-mcp-oauth21.md).
/// Registered unconditionally (like the entities and the cleanup service) — the
/// <c>McpOAuth:Enabled</c> flag is read at the controller/middleware level, not here, so
/// flipping it at runtime never requires a different DI graph.
/// </summary>
public static class McpOAuthServiceExtensions
{
    public static IServiceCollection AddMcpOAuth(this IServiceCollection services, IConfiguration configuration)
    {
        services.Configure<McpOAuthOptions>(configuration.GetSection(McpOAuthOptions.SectionName));

        services.AddSingleton<IOAuthClientResolver, OAuthClientResolver>();

        // Scoped like ApiKeyService — both hold a MasterDbContext, which is itself scoped.
        services.AddScoped<IOAuthService, OAuthService>();

        // Dedicated named HttpClient for CIMD fetches, wired to the SSRF-safe connect callback
        // (SsrfSafeConnect) instead of the framework default. A dedicated client — not a shared
        // one — is deliberate: nothing else in the process may ever reuse this handler for a
        // request whose target host is NOT re-validated per connection the way this one is.
        services.AddHttpClient(OAuthClientResolver.HttpClientName)
            .ConfigurePrimaryHttpMessageHandler(() => new SocketsHttpHandler
            {
                AllowAutoRedirect = false,
                ConnectCallback = async (context, ct) =>
                {
                    var safeAddress = await SsrfSafeConnect.ResolveSafeAddressAsync(context.DnsEndPoint.Host, ct);

                    var socket = new System.Net.Sockets.Socket(
                        System.Net.Sockets.SocketType.Stream, System.Net.Sockets.ProtocolType.Tcp)
                    {
                        NoDelay = true
                    };

                    try
                    {
                        // Connecting to the address we just validated — not to a fresh DNS lookup
                        // of the hostname — is what closes the rebinding TOCTOU window (ADR §4.6).
                        await socket.ConnectAsync(safeAddress, context.DnsEndPoint.Port, ct);
                        return new System.Net.Sockets.NetworkStream(socket, ownsSocket: true);
                    }
                    catch
                    {
                        socket.Dispose();
                        throw;
                    }
                }
            });

        return services;
    }
}

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

        // Stateless (just wraps IDataProtectionProvider, itself a singleton) — no reason to
        // build a new one per request.
        services.AddSingleton<IOAuthAuthorizeTicketProtector, OAuthAuthorizeTicketProtector>();

        services.AddOAuthClientMetadataHttpClient();

        return services;
    }
}

using Fakvio.McpServer.Client;
using Fakvio.McpServer.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Fakvio.McpServer;

/// <summary>
/// The DI registration both transports share — settings, the authenticated API client, and
/// the MCP server with every discovered tool.
///
/// <para>
/// It lives in one method because the two hosts (stdio console vs. ASP.NET Core web app)
/// must expose the <b>same</b> tool surface; a second, hand-copied registration would drift
/// the moment one of them changed. The unit tests call this same method, so what they assert
/// is what the servers actually run.
/// </para>
/// </summary>
public static class McpServerRegistration
{
    /// <summary>Name and version reported to the AI client in the initialize handshake.</summary>
    private const string ServerName = "fakvio";
    private const string ServerVersion = "1.0.0";

    /// <summary>
    /// Registers everything that is identical in stdio and HTTP mode.
    ///
    /// <para>
    /// <b>Caller's responsibility:</b> register an <see cref="IApiTokenProvider"/> — it is the
    /// one piece that genuinely differs between the modes (process credential vs. caller
    /// credential), so it is deliberately not chosen here.
    /// </para>
    /// </summary>
    /// <returns>
    /// The MCP builder, so the caller can chain the transport
    /// (<c>WithStdioServerTransport()</c> or <c>WithHttpTransport()</c>).
    /// </returns>
    public static IMcpServerBuilder AddFakvioMcpServer(
        this IServiceCollection services, McpServerSettings settings)
    {
        services.AddSingleton(settings);
        services.AddTransient<AuthHeaderHandler>();

        // Typed HttpClient for IFakvioApiClient. AuthHeaderHandler stamps the Authorization
        // header per request from IApiTokenProvider — never HttpClient.DefaultRequestHeaders,
        // which is shared by every caller of the client (see AuthHeaderHandler for the why).
        services.AddHttpClient<IFakvioApiClient, FakvioApiClient>(client =>
            {
                client.BaseAddress = new Uri(settings.ApiBaseUrl.TrimEnd('/') + "/");
                client.DefaultRequestHeaders.Accept.Add(
                    new System.Net.Http.Headers.MediaTypeWithQualityHeaderValue("application/json"));
            })
            .AddHttpMessageHandler<AuthHeaderHandler>();

        // ORDER MATTERS: IFakvioApiClient must already be in the container when AddMcpServer()
        // runs. The SDK asks IServiceProviderIsService whether a tool parameter can be resolved
        // from DI; if it can, the parameter is injected and hidden from the tool's input schema.
        // Register the client afterwards and every tool would advertise an "api" parameter the
        // AI client cannot possibly supply — i.e. all 37 tools become uncallable.
        return services
            .AddMcpServer(options =>
            {
                options.ServerInfo = new()
                {
                    Name = ServerName,
                    Version = ServerVersion
                };
            })
            .WithToolsFromAssembly(typeof(McpServerRegistration).Assembly);
    }
}

using Fakvio.McpServer.Configuration;

namespace Fakvio.McpServer.Client;

/// <summary>
/// stdio-mode token provider: hands back the token read from the
/// FAKVIO_API_TOKEN environment variable at startup.
///
/// This is safe here — and only here — because a stdio MCP server process is
/// started by (and serves) a single AI client, so "the process credential" and
/// "the caller's credential" are the same thing. Under HTTP hosting they are
/// not, which is why the token flows through <see cref="IApiTokenProvider"/>
/// rather than being baked into the HttpClient.
/// </summary>
public sealed class EnvironmentApiTokenProvider : IApiTokenProvider
{
    private readonly McpServerSettings _settings;

    public EnvironmentApiTokenProvider(McpServerSettings settings)
    {
        _settings = settings;
    }

    /// <inheritdoc />
    public string? GetToken() => _settings.ApiToken;
}

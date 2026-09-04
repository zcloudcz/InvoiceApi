namespace Fakvio.McpServer.Configuration;

/// <summary>
/// Configuration POCO for the MCP server, plus the one place its environment contract lives:
///   - FAKVIO_API_URL   → ApiBaseUrl (default: <see cref="DefaultApiBaseUrl"/>)
///   - FAKVIO_API_TOKEN → ApiToken   (required only in stdio mode; accepts an API key or a JWT)
///
/// Junior note: the class holds settings and one factory that reads them from the environment.
/// The DI container creates one instance (built by <see cref="FromEnvironment"/> at startup)
/// and injects it where needed.
/// </summary>
public class McpServerSettings
{
    /// <summary>Environment variable that overrides <see cref="ApiBaseUrl"/>.</summary>
    public const string ApiUrlEnv = "FAKVIO_API_URL";

    /// <summary>Environment variable that supplies <see cref="ApiToken"/>.</summary>
    public const string ApiTokenEnv = "FAKVIO_API_TOKEN";

    /// <summary>
    /// The single place the default API base URL is written down — used both by the property
    /// initializer below and by <see cref="FromEnvironment"/>, so the two cannot drift apart.
    /// That drift is what #257 was: two separate literals, and only the one startup never reads
    /// got fixed first.
    /// <para>
    /// Value matches the local <c>Fakvio.API</c> <c>https</c> launch profile
    /// (see <c>Fakvio.API/Properties/launchSettings.json</c>).
    /// </para>
    /// </summary>
    public const string DefaultApiBaseUrl = "https://localhost:7047";

    /// <summary>
    /// Base URL of the Fakvio REST API (e.g., "https://localhost:7047").
    /// Defaults to <see cref="DefaultApiBaseUrl"/> for local development.
    /// </summary>
    public string ApiBaseUrl { get; set; } = DefaultApiBaseUrl;

    /// <summary>
    /// Bearer credential used to authenticate API requests — either a <c>fak_live_…</c> API key
    /// or a JWT; the API picks the scheme from the prefix.
    /// Required in stdio mode only (the server fails fast at startup if it is missing); in HTTP
    /// mode the credential arrives per request and this value is unused.
    /// </summary>
    public string ApiToken { get; set; } = string.Empty;

    /// <summary>
    /// Builds the settings the process actually runs with, from the environment.
    /// <para>
    /// Junior note: this lives here rather than inline in <c>Program.cs</c> so it can be tested.
    /// <c>Program.cs</c> is an entry point with top-level statements — nothing can call into it,
    /// so a default resolved there is a default no test ever executes (see
    /// <c>Fakvio.Tests.Unit/McpServer/McpServerSettingsTests.cs</c>). Same shape as
    /// <c>TailscaleTunnel.ResolveHostname()</c>.
    /// </para>
    /// </summary>
    public static McpServerSettings FromEnvironment() => new()
    {
        ApiBaseUrl = Environment.GetEnvironmentVariable(ApiUrlEnv) ?? DefaultApiBaseUrl,
        ApiToken = Environment.GetEnvironmentVariable(ApiTokenEnv) ?? string.Empty
    };
}

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
    /// Environment variable that turns on MCP OAuth 2.1 support (ADR 0001,
    /// docs/adr/0001-mcp-oauth21.md §5.1) — Protected Resource Metadata, the
    /// <c>resource_metadata</c> challenge parameter, and the audience check on OAuth tokens.
    /// Default off: <c>false</c> reproduces today's response exactly.
    /// </summary>
    public const string OAuthEnabledEnv = "FAKVIO_MCP_OAUTH_ENABLED";

    /// <summary>Environment variable supplying <see cref="PublicUrl"/> — this host's own externally-reachable base URL.</summary>
    public const string PublicUrlEnv = "FAKVIO_MCP_PUBLIC_URL";

    /// <summary>Environment variable supplying <see cref="OAuthIssuer"/> — the authorization server's issuer URL.</summary>
    public const string OAuthIssuerEnv = "FAKVIO_OAUTH_ISSUER";

    /// <summary>
    /// Environment variable supplying <see cref="ResourceProofSecret"/> — the shared secret
    /// added to every outgoing API request as <c>X-Fakvio-Resource-Proof</c> (ADR §4.4, threat T6).
    /// Must equal the API host's <c>McpOAuth:ResourceProofSecret</c>.
    /// </summary>
    public const string ResourceProofSecretEnv = "FAKVIO_MCP_RESOURCE_PROOF_SECRET";

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
    /// Whether tools may read files from the server's own disk (e.g. the <c>filePath</c>
    /// argument of <c>upload_received_invoice_attachment</c>).
    /// True only in stdio mode, where the process runs on the user's machine and "the server's
    /// disk" is the user's disk. In HTTP mode the disk belongs to the shared host — a path
    /// argument there would let any caller read files of another tenant's process, so it stays off.
    /// </summary>
    public bool AllowLocalFiles { get; set; }

    /// <summary>Master switch for OAuth support on this host — see <see cref="OAuthEnabledEnv"/>. Default off.</summary>
    public bool OAuthEnabled { get; set; }

    /// <summary>This host's own externally-reachable base URL, e.g. <c>https://mcp.fakvio.cz</c> — used to build the PRM's <c>resource</c> and the challenge's <c>resource_metadata</c> URL.</summary>
    public string? PublicUrl { get; set; }

    /// <summary>The authorization server's issuer, e.g. <c>https://api.fakvio.cz</c> — published in PRM's <c>authorization_servers</c>.</summary>
    public string? OAuthIssuer { get; set; }

    /// <summary>Shared secret proving to the API that a request really came from this MCP host (ADR §4.4). Null/empty = the header is not sent.</summary>
    public string? ResourceProofSecret { get; set; }

    /// <summary>
    /// Builds the settings the process actually runs with, from the environment.
    /// <para>
    /// Junior note: this lives here rather than inline in <c>Program.cs</c> so it can be tested.
    /// <c>Program.cs</c> is an entry point with top-level statements — nothing can call into it,
    /// so a default resolved there is a default no test ever executes (see
    /// <c>Fakvio.Tests.Unit/McpServer/McpServerSettingsTests.cs</c>).
    /// </para>
    /// </summary>
    public static McpServerSettings FromEnvironment() => new()
    {
        ApiBaseUrl = Environment.GetEnvironmentVariable(ApiUrlEnv) ?? DefaultApiBaseUrl,
        ApiToken = Environment.GetEnvironmentVariable(ApiTokenEnv) ?? string.Empty,
        // bool.TryParse rather than a truthy string check: an unrecognized value (typo,
        // "1"/"yes") must fail closed to "off" like every other flag in this ADR, not silently
        // parse as false via a loose comparison that looks like it handles more cases than it does.
        OAuthEnabled = bool.TryParse(Environment.GetEnvironmentVariable(OAuthEnabledEnv), out var oauthEnabled) && oauthEnabled,
        PublicUrl = Environment.GetEnvironmentVariable(PublicUrlEnv),
        OAuthIssuer = Environment.GetEnvironmentVariable(OAuthIssuerEnv),
        ResourceProofSecret = Environment.GetEnvironmentVariable(ResourceProofSecretEnv)
    };
}

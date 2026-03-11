namespace Fakvio.McpServer.Configuration;

/// <summary>
/// Configuration POCO for the MCP server.
/// Values are read from environment variables at startup:
///   - FAKVIO_API_URL  → ApiBaseUrl  (default: https://localhost:7001)
///   - FAKVIO_API_TOKEN → ApiToken   (required — JWT bearer token)
///
/// Junior note: This class holds settings but has no logic.
/// The DI container creates one instance and injects it where needed.
/// </summary>
public class McpServerSettings
{
    /// <summary>
    /// Base URL of the Fakvio REST API (e.g., "https://localhost:7001").
    /// Defaults to localhost for local development.
    /// </summary>
    public string ApiBaseUrl { get; set; } = "https://localhost:7001";

    /// <summary>
    /// JWT bearer token used to authenticate API requests.
    /// Must be set via the FAKVIO_API_TOKEN environment variable.
    /// The server will fail fast at startup if this is missing.
    /// </summary>
    public string ApiToken { get; set; } = string.Empty;
}

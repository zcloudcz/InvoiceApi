namespace Fakvio.Infrastructure.Authentication.OAuth;

/// <summary>
/// Configuration for MCP OAuth 2.1 (ADR 0001, docs/adr/0001-mcp-oauth21.md §5.1).
/// Bound from the "McpOAuth" configuration section on the API host.
///
/// <see cref="Enabled"/> is the master switch and defaults to <c>false</c> — with it off,
/// every OAuth endpoint and the AS/PRM metadata documents answer 404 and
/// <c>fak_oat_…</c> tokens are rejected, so today's API-key-only behaviour is completely
/// unchanged (see the "flag off" tests in N5.3/N5.6).
/// </summary>
public class McpOAuthOptions
{
    public const string SectionName = "McpOAuth";

    /// <summary>Master switch. Default OFF — see class docs.</summary>
    public bool Enabled { get; set; }

    /// <summary>
    /// GA switch (Q8 in the ADR): once true, <see cref="AllowedUserIds"/> and
    /// <see cref="AllowedCompanyIds"/> are ignored and every active user may consent.
    /// </summary>
    public bool AllowAll { get; set; }

    /// <summary>Users allowed to grant/refresh OAuth consent while <see cref="AllowAll"/> is false.</summary>
    public long[] AllowedUserIds { get; set; } = [];

    /// <summary>Same as <see cref="AllowedUserIds"/>, but for every user of the given company.</summary>
    public long[] AllowedCompanyIds { get; set; } = [];

    /// <summary>
    /// Hostnames a client_id's CIMD URL is allowed to live on (ADR §4.6, Q3). Case-insensitive,
    /// exact match — no wildcard subdomains, so a compromised subdomain of an otherwise trusted
    /// host cannot register a client.
    /// </summary>
    public string[] TrustedClientHosts { get; set; } = ["claude.ai", "chatgpt.com"];

    /// <summary>The AS issuer, e.g. <c>https://api.fakvio.cz</c>. Must be set before <see cref="Enabled"/> can be turned on.</summary>
    public string? Issuer { get; set; }

    /// <summary>The canonical MCP resource URL, e.g. <c>https://mcp.fakvio.cz/mcp</c> (RFC 8707).</summary>
    public string? Resource { get; set; }

    /// <summary>
    /// Base URL of the consent page on the WASM app, e.g. <c>https://app.fakvio.cz/oauth/consent</c>
    /// (ADR §4.1 "Consent"). <c>GET /oauth/authorize</c> redirects here with a <c>?ticket=</c> query param.
    /// </summary>
    public string? ConsentUrl { get; set; }

    /// <summary>
    /// Shared secret between the API and the MCP host (ADR §4.4 — the "resource proof" header
    /// that keeps a leaked OAuth token from working directly against the REST API). Key Vault
    /// reference in production; see ADMINGUIDE runbook.
    /// </summary>
    public string? ResourceProofSecret { get; set; }
}

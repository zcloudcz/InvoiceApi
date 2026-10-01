namespace Fakvio.Contracts.Dto.OAuth;

/// <summary>
/// One row of "Připojené aplikace" (ADR 0001, docs/adr/0001-mcp-oauth21.md §4.8) — a live OAuth
/// consent the current user granted. Never includes a token or any secret; this is display data
/// only, backed by <c>GET /api/oauth/grants</c>.
/// </summary>
public class OAuthGrantDto
{
    public long Id { get; set; }

    /// <summary>The client_id URL — same "trust this, not the display name" reasoning as the consent screen.</summary>
    public string ClientId { get; set; } = string.Empty;

    /// <summary>Self-asserted display name, snapshotted at consent time.</summary>
    public string ClientName { get; set; } = string.Empty;

    /// <summary>Granted scopes ("read" or "read,write").</summary>
    public string Scopes { get; set; } = string.Empty;

    public DateTime CreatedAt { get; set; }

    public DateTime? LastUsedAt { get; set; }
}

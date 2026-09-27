namespace Fakvio.Application.Service;

/// <summary>
/// A resolved Client ID Metadata Document (CIMD) — the OAuth client's self-published identity,
/// per ADR 0001 (docs/adr/0001-mcp-oauth21.md) §4.6.
/// </summary>
/// <param name="ClientId">Echoes the requested client_id — the resolver has already verified this equals the document's own "client_id" field.</param>
/// <param name="ClientName">Self-asserted display name. NEVER shown as the primary identity on the consent screen (ADR §4.9) — only <paramref name="ClientId"/> is trusted for that.</param>
/// <param name="RedirectUris">The client's declared redirect URIs — the authorize endpoint accepts only an exact match against one of these.</param>
public sealed record OAuthClientDocument(string ClientId, string ClientName, IReadOnlyList<string> RedirectUris);

/// <summary>
/// Resolves an OAuth client_id (a CIMD URL) to its metadata document, per ADR 0001
/// (docs/adr/0001-mcp-oauth21.md) §4.6.
///
/// Fail-closed and deliberately uninformative on failure: every reason a client_id could be
/// refused (malformed URL, host not on the allowlist, SSRF-blocked target, unreachable server,
/// malformed document, auth method we do not support, client_id mismatch) collapses to
/// <c>null</c>. The caller only ever learns "this client_id does not resolve"; the specific
/// reason goes to the log as <c>OAuth.ClientRejected</c> (ADR §4.11) — the same shape as
/// <c>ApiKeyAuthenticator.AuthenticateAsync</c>.
/// </summary>
public interface IOAuthClientResolver
{
    Task<OAuthClientDocument?> ResolveAsync(string clientId, CancellationToken ct = default);
}

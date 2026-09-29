namespace Fakvio.Application.Exceptions;

/// <summary>
/// Thrown by <c>IOAuthService</c> for every rejection at the token/revoke endpoints (ADR 0001,
/// docs/adr/0001-mcp-oauth21.md §4.2). <see cref="ErrorCode"/> is always one of the RFC 6749 §5.2
/// error codes — "Chyby jen RFC 6749 kódy bez interních detailů" (ADR §4.2): the controller maps
/// this straight to the JSON error body and nothing more specific ever reaches the client,
/// exactly like <c>ApiKeyAuthenticator</c> never tells a caller WHY a key failed.
/// </summary>
public sealed class OAuthErrorException : Exception
{
    /// <summary>An RFC 6749 §5.2 error code (e.g. "invalid_grant", "invalid_client", "invalid_scope", "invalid_target").</summary>
    public string ErrorCode { get; }

    /// <param name="errorCode">The RFC 6749 error code returned to the client.</param>
    /// <param name="diagnosticMessage">Detail for the SERVER log only — never sent to the client.</param>
    public OAuthErrorException(string errorCode, string diagnosticMessage) : base(diagnosticMessage)
    {
        ErrorCode = errorCode;
    }

    public const string InvalidRequest = "invalid_request";
    public const string InvalidClient = "invalid_client";
    public const string InvalidGrant = "invalid_grant";
    public const string InvalidScope = "invalid_scope";
    public const string InvalidTarget = "invalid_target";
    public const string UnsupportedGrantType = "unsupported_grant_type";
    public const string AccessDenied = "access_denied";
}

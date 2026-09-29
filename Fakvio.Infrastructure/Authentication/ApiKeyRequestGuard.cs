using System.Security.Claims;
using Microsoft.AspNetCore.Http;

namespace Fakvio.Infrastructure.Authentication;

/// <summary>
/// Decides what a request authenticated with an API key is allowed to do, and answers
/// 403 when it is not. Shared verbatim by both hosts (see CLAUDE.md — "API + Functions
/// duplication"); the API host wraps it in <c>ApiKeyScopeMiddleware</c>, the Functions
/// host calls it at the end of its authentication middleware.
///
/// Requests authenticated with a JWT are left completely alone — they have no scope
/// claim, and their permissions are the user's role, unchanged.
/// </summary>
public static class ApiKeyRequestGuard
{
    /// <summary>
    /// POST endpoints that compute an answer instead of changing state, so a read-only
    /// key may call them.
    ///
    /// The list is deliberately tiny and the default is deny: the cost of forgetting an
    /// entry is a read-only key getting a 403 on a calculator, while the cost of a wrong
    /// entry is a read-only key writing data. Add a path only after checking that the
    /// action persists nothing — "test" and "preview" endpoints are not automatically safe.
    /// </summary>
    private static readonly string[] SafeMethodOverridePaths =
    [
        "/api/tax/estimate" // pure calculation over the posted figures — TaxController.Estimate
    ];

    /// <summary>
    /// The one key-management endpoint an API key may call: it only reports back who the
    /// caller is. Everything else under /api/api-key stays JWT-only, so a leaked key
    /// cannot mint a fresh key for itself or hide its tracks by revoking others.
    /// </summary>
    private const string SelfDescribePath = "/api/api-key/me";

    /// <summary>
    /// Path prefix of the key-management endpoints.
    /// </summary>
    private const string ManagementPathPrefix = "/api/api-key";

    /// <summary>
    /// Path prefix of the OAuth grant-management endpoints ("Připojené aplikace" —
    /// ADR 0001 §4.5/§4.8). Same reasoning as <see cref="ManagementPathPrefix"/>: a
    /// credential authenticated via scope (API key OR OAuth access token) must not be
    /// able to list or revoke grants — only a first-party user session (JWT) may.
    /// </summary>
    private const string OAuthGrantManagementPathPrefix = "/api/oauth/grants";

    /// <summary>
    /// Returns the reason the request must be refused, or null when it may proceed.
    /// Pure function — the middlewares only translate a non-null result into a 403.
    /// </summary>
    public static string? GetDenialReason(ClaimsPrincipal user, string method, string path)
    {
        var scopes = user.FindFirst(ApiKeyAuthenticationDefaults.ScopeClaimType)?.Value;

        // No scope claim = not an API key (JWT, or anonymous). Nothing to enforce.
        if (scopes is null)
            return null;

        var normalizedPath = path.ToLowerInvariant();

        if (normalizedPath.StartsWith(ManagementPathPrefix, StringComparison.Ordinal)
            && !normalizedPath.Equals(SelfDescribePath, StringComparison.Ordinal))
            return "API keys cannot manage API keys. Use a user session (JWT) to create or revoke keys.";

        if (normalizedPath.StartsWith(OAuthGrantManagementPathPrefix, StringComparison.Ordinal))
            return "OAuth grants can only be managed with a user session (JWT), not an API key or OAuth access token.";

        if (!RequiresWriteScope(method, normalizedPath))
            return null;

        return HasWriteScope(scopes)
            ? null
            : "This API key is read-only. A key with the 'write' scope is required for this operation.";
    }

    /// <summary>
    /// True when the request changes state. Everything that is not a safe HTTP method
    /// counts, minus the explicitly listed calculation endpoints.
    /// </summary>
    private static bool RequiresWriteScope(string method, string normalizedPath)
    {
        if (HttpMethods.IsGet(method) || HttpMethods.IsHead(method) || HttpMethods.IsOptions(method))
            return false;

        return !SafeMethodOverridePaths.Contains(normalizedPath);
    }

    /// <summary>
    /// Checks the stored scope string for the write scope. Split on ',' rather than
    /// substring-matching so that a future scope whose name contains "write" cannot
    /// grant write access by accident.
    /// </summary>
    private static bool HasWriteScope(string scopes)
        => scopes
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Any(s => string.Equals(s, ApiKeyAuthenticationDefaults.WriteScope, StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// Applies <see cref="GetDenialReason"/> to a live request and writes the 403 when it
    /// is refused. Returns true when the response has been written and the pipeline must
    /// stop.
    /// </summary>
    public static async Task<bool> TryRejectAsync(HttpContext context)
    {
        var reason = GetDenialReason(
            context.User,
            context.Request.Method,
            context.Request.Path.Value ?? string.Empty);

        if (reason is null)
            return false;

        context.Response.StatusCode = StatusCodes.Status403Forbidden;
        await context.Response.WriteAsJsonAsync(new { message = reason });
        return true;
    }
}

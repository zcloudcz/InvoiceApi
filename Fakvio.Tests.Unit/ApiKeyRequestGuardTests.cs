using System.Security.Claims;
using System.Text.Json;
using Fakvio.Infrastructure.Authentication;
using Microsoft.AspNetCore.Http;
using Shouldly;

namespace Fakvio.Tests.Unit;

/// <summary>
/// Unit tests for ApiKeyRequestGuard — the scope rules both hosts share.
///
/// The rule under test is "effective permissions = role ∩ scope": a read-only key may
/// not change state, no key may manage keys, and a JWT session is not affected at all.
/// </summary>
public class ApiKeyRequestGuardTests
{
    private const string ReadOnly = "read";
    private const string ReadWrite = "read,write";

    /// <summary>
    /// Principal shaped exactly like the one ApiKeyAuthenticator builds. Passing null
    /// scopes produces a JWT-style principal (no scope claim at all).
    /// </summary>
    private static ClaimsPrincipal Principal(string? scopes)
    {
        var claims = new List<Claim> { new(ClaimTypes.Role, "User") };

        if (scopes is not null)
            claims.Add(new Claim(ApiKeyAuthenticationDefaults.ScopeClaimType, scopes));

        return new ClaimsPrincipal(new ClaimsIdentity(claims, "TestAuth"));
    }

    // ─── Safe methods ────────────────────────────────────────────────────────

    [Theory]
    [InlineData("GET")]
    [InlineData("HEAD")]
    [InlineData("OPTIONS")]
    public void ReadOnlyKey_MayUseSafeMethods(string method)
    {
        ApiKeyRequestGuard.GetDenialReason(Principal(ReadOnly), method, "/api/invoice/paged")
            .ShouldBeNull();
    }

    // ─── Write scope ─────────────────────────────────────────────────────────

    [Theory]
    [InlineData("POST")]
    [InlineData("PUT")]
    [InlineData("PATCH")]
    [InlineData("DELETE")]
    public void ReadOnlyKey_IsRefusedOnStateChangingMethods(string method)
    {
        ApiKeyRequestGuard.GetDenialReason(Principal(ReadOnly), method, "/api/invoice")
            .ShouldNotBeNull();
    }

    [Theory]
    [InlineData("POST")]
    [InlineData("PUT")]
    [InlineData("PATCH")]
    [InlineData("DELETE")]
    public void ReadWriteKey_MayChangeState(string method)
    {
        ApiKeyRequestGuard.GetDenialReason(Principal(ReadWrite), method, "/api/invoice")
            .ShouldBeNull();
    }

    [Fact]
    public void UnknownMethod_DefaultsToRequiringWrite()
    {
        // Fail closed: anything not explicitly recognised as safe needs the write scope.
        ApiKeyRequestGuard.GetDenialReason(Principal(ReadOnly), "PROPFIND", "/api/invoice")
            .ShouldNotBeNull();
    }

    // ─── SafeMethodOverridePaths ─────────────────────────────────────────────

    [Fact]
    public void ReadOnlyKey_MayPostToACalculationEndpoint()
    {
        // POST /api/tax/estimate computes an answer over the posted figures and persists
        // nothing — refusing it would make a read-only key useless for tax questions.
        ApiKeyRequestGuard.GetDenialReason(Principal(ReadOnly), "POST", "/api/tax/estimate")
            .ShouldBeNull();
    }

    [Fact]
    public void SafeMethodOverride_IsCaseInsensitive()
    {
        ApiKeyRequestGuard.GetDenialReason(Principal(ReadOnly), "POST", "/API/Tax/Estimate")
            .ShouldBeNull();
    }

    [Fact]
    public void SafeMethodOverride_DoesNotLeakToSiblingPaths()
    {
        // The override is an exact path, not a prefix: /api/tax/config writes configuration.
        ApiKeyRequestGuard.GetDenialReason(Principal(ReadOnly), "POST", "/api/tax/config")
            .ShouldNotBeNull();
    }

    // ─── Key management is JWT-only ──────────────────────────────────────────

    [Theory]
    [InlineData("GET", "/api/api-key")]
    [InlineData("POST", "/api/api-key")]
    [InlineData("POST", "/api/api-key/7/revoke")]
    public void ApiKey_CannotManageApiKeys_EvenWithWriteScope(string method, string path)
    {
        // A leaked key must not be able to mint itself a fresh one or revoke the others.
        ApiKeyRequestGuard.GetDenialReason(Principal(ReadWrite), method, path)
            .ShouldNotBeNull();
    }

    [Fact]
    public void ApiKey_MayAskWhoItIs()
    {
        // The one exception: /me only reports back the principal.
        ApiKeyRequestGuard.GetDenialReason(Principal(ReadOnly), "GET", "/api/api-key/me")
            .ShouldBeNull();
    }

    // ─── JWT is untouched ────────────────────────────────────────────────────

    [Theory]
    [InlineData("POST", "/api/invoice")]
    [InlineData("POST", "/api/api-key")]
    [InlineData("DELETE", "/api/client/1")]
    public void JwtPrincipal_IsNeverRestricted(string method, string path)
    {
        // No scope claim = not an API key. The JWT path must behave exactly as before.
        ApiKeyRequestGuard.GetDenialReason(Principal(scopes: null), method, path)
            .ShouldBeNull();
    }

    [Fact]
    public void AnonymousPrincipal_IsNotRestricted()
    {
        // Authentication decides what anonymous may do; the guard has no opinion.
        ApiKeyRequestGuard.GetDenialReason(new ClaimsPrincipal(new ClaimsIdentity()), "POST", "/api/auth/login")
            .ShouldBeNull();
    }

    // ─── Response shape ──────────────────────────────────────────────────────

    [Fact]
    public async Task TryRejectAsync_WritesA403WithAMessage()
    {
        var context = new DefaultHttpContext();
        context.User = Principal(ReadOnly);
        context.Request.Method = "POST";
        context.Request.Path = "/api/invoice";
        context.Response.Body = new MemoryStream();

        var rejected = await ApiKeyRequestGuard.TryRejectAsync(context);

        rejected.ShouldBeTrue();
        context.Response.StatusCode.ShouldBe(StatusCodes.Status403Forbidden);

        context.Response.Body.Position = 0;
        var body = await JsonDocument.ParseAsync(context.Response.Body);
        body.RootElement.GetProperty("message").GetString().ShouldContain("read-only");
    }

    [Fact]
    public async Task TryRejectAsync_LetsAnAllowedRequestThrough()
    {
        var context = new DefaultHttpContext();
        context.User = Principal(ReadWrite);
        context.Request.Method = "POST";
        context.Request.Path = "/api/invoice";

        (await ApiKeyRequestGuard.TryRejectAsync(context)).ShouldBeFalse();
        context.Response.StatusCode.ShouldBe(StatusCodes.Status200OK);
    }
}

using Fakvio.Infrastructure.Authentication;
using Shouldly;

namespace Fakvio.Tests.Unit;

/// <summary>
/// Unit tests for ApiKeyAuthenticationDefaults.ExtractRawKey — the single place that
/// decides "does this request present an API key?".
///
/// Three call sites depend on that one answer: the policy scheme's ForwardDefaultSelector
/// (API host), ApiKeyAuthenticationHandler (API host) and ApiKeyAuthenticationMiddleware
/// (Functions host). They must never disagree, so the routing decision is pinned here
/// rather than only through the two hosts' pipelines.
///
/// The failure modes this protects against are asymmetric:
/// - too strict → a valid key is handed to the JWT validator and its owner gets a 401;
/// - too loose  → a token that is not a key is hashed and looked up as one.
/// Both end in a refused request, which is why the discriminator is easy to get subtly
/// wrong without anything going red.
/// </summary>
public class ApiKeyAuthenticationDefaultsTests
{
    private const string RawKey = "fak_live_AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA";
    private const string JwtToken = "eyJhbGciOiJIUzI1NiJ9.e30.signature";

    // ─── Recognised as an API key ────────────────────────────────────────────

    [Theory]
    [InlineData("Bearer ")]
    [InlineData("bearer ")]   // clients are free to lower-case the scheme (RFC 7235 §2.1)
    [InlineData("BEARER ")]
    public void BearerSchemeIsCaseInsensitive(string scheme)
    {
        ApiKeyAuthenticationDefaults.ExtractRawKey(scheme + RawKey).ShouldBe(RawKey);
    }

    [Fact]
    public void SurroundingWhitespaceIsStrippedFromTheToken()
    {
        // A stray space would otherwise change the hash and turn a valid key into a 401.
        ApiKeyAuthenticationDefaults.ExtractRawKey($"Bearer   {RawKey}  ").ShouldBe(RawKey);
    }

    // ─── Not an API key: left to the JWT branch ──────────────────────────────

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("Bearer")]                  // scheme without a token
    [InlineData("Bearer ")]                 // scheme with an empty token
    [InlineData("Basic ZmFrOmtleQ==")]      // a different scheme entirely
    [InlineData($"Basic {RawKey}")]         // right token, wrong scheme
    [InlineData(RawKey)]                    // bare key, no scheme
    [InlineData($"Bearer {JwtToken}")]      // the ordinary session token
    public void NonApiKeyHeaderYieldsNull(string? header)
    {
        ApiKeyAuthenticationDefaults.ExtractRawKey(header).ShouldBeNull();
    }

    [Fact]
    public void KeyPrefixIsCaseSensitive()
    {
        // The prefix is compared with Ordinal on purpose: the key alphabet is
        // case-sensitive, so "FAK_…" is not a mis-typed key we should hash and look up —
        // it is something else, and it belongs on the JWT branch (which refuses it).
        ApiKeyAuthenticationDefaults.ExtractRawKey("Bearer FAK_live_AAAA").ShouldBeNull();
    }

    // ─── Two Authorization headers ───────────────────────────────────────────
    //
    // ASP.NET Core joins repeated headers with ", ", so a client that sends both a JWT
    // and a key hands the discriminator one string holding two tokens. Neither order may
    // produce a usable credential — the point is that both fail closed.

    [Fact]
    public void JwtFollowedByAKeyIsNotTreatedAsAKey()
    {
        // Falls through to the JWT branch, which cannot parse the joined value → 401.
        ApiKeyAuthenticationDefaults.ExtractRawKey($"Bearer {JwtToken}, Bearer {RawKey}")
            .ShouldBeNull();
    }

    [Fact]
    public void KeyFollowedByAJwtDoesNotYieldTheCleanKey()
    {
        // Taken as an API key, but the trailing junk is part of the token, so the hash
        // lookup misses and the request is refused. What must never happen is the joined
        // value being silently trimmed back to the valid key.
        ApiKeyAuthenticationDefaults.ExtractRawKey($"Bearer {RawKey}, Bearer {JwtToken}")
            .ShouldBe($"{RawKey}, Bearer {JwtToken}");
    }
}

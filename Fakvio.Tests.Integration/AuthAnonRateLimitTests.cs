using System.Net;
using System.Net.Http.Json;
using Fakvio.Contracts.Dto.Auth;
using Fakvio.Tests.Integration.Fixtures;
using Microsoft.AspNetCore.Hosting;
using Shouldly;

namespace Fakvio.Tests.Integration;

/// <summary>
/// RC.4 — per-IP rate limiting ("auth-anon" policy, <c>Fakvio.API/Program.cs</c>) in
/// front of the anonymous auth endpoints.
///
/// THE GAP: reCAPTCHA (CaptchaService) was the only defense on login/register/ares, and
/// forgot-password had none at all (see ForgotPasswordCaptchaTests, RC.3). Neither one
/// bounds the ATTEMPT RATE — an attacker who has a working token (or a bypassed/disabled
/// gate — production runs with Recaptcha:Enabled=false today) can still fire requests
/// as fast as the network allows.
///
/// THE FIX: a fixed-window limiter, partitioned by client IP, applied via
/// [EnableRateLimiting("auth-anon")] on every anonymous auth endpoint. The base
/// FakvioFactory widens the limit to effectively "off" (see InvoiceApiFactory) so the
/// rest of the suite — which calls /api/auth/login constantly via AuthHelper — is not
/// affected; this class dials it back down to test the limiter itself.
///
/// Deliberately NOT an IClassFixture: the limiter's counter lives server-side, keyed only
/// by client IP (TestServer requests all share one partition — no real per-request IP),
/// so a shared factory across [Fact]s would make one test's exhausted counter leak into
/// the next depending on run order. A fresh factory per test gives each one a clean
/// server-side counter, at the cost of paying host startup again — cheap here (InMemory
/// database, no real network) and worth the isolation.
///
/// WHAT THESE TESTS PIN:
///   - the (PermitLimit + 1)-th request in the window gets 429 with Retry-After,
///   - a DIFFERENT endpoint (validate — no [EnableRateLimiting] here) is unaffected by
///     the same caller having exhausted the login policy,
///   - the reCAPTCHA gate still runs independently (Recaptcha:Enabled=false in the test
///     host — see base factory — so it is not what is rejecting requests below).
/// </summary>
public class AuthAnonRateLimitTests
{
    private const int PermitLimit = 3;

    [Fact]
    public async Task Login_ExceedsPermitLimit_ReturnsTooManyRequests_WithRetryAfter()
    {
        using var factory = new TightRateLimitFactory();
        var client = factory.CreateClient();
        var loginRequest = new LoginRequest { Email = "nobody@example.com", Password = "wrong" };

        for (var i = 0; i < PermitLimit; i++)
        {
            var response = await client.PostAsJsonAsync("/api/auth/login", loginRequest);
            // Wrong credentials — 401, not 429. The limiter must not trip before the limit.
            response.StatusCode.ShouldBe(HttpStatusCode.Unauthorized,
                $"request #{i + 1} of {PermitLimit} is within the permit limit");
        }

        var rejected = await client.PostAsJsonAsync("/api/auth/login", loginRequest);

        rejected.StatusCode.ShouldBe(HttpStatusCode.TooManyRequests);
        rejected.Headers.RetryAfter.ShouldNotBeNull();
    }

    [Fact]
    public async Task Login_ExceedsPermitLimit_DoesNotAffectValidateEndpoint()
    {
        // Login has no exemption once it is starved; /api/auth/validate has no
        // [EnableRateLimiting] attribute at all, so it must keep answering regardless.
        using var factory = new TightRateLimitFactory();
        var client = factory.CreateClient();
        var loginRequest = new LoginRequest { Email = "nobody2@example.com", Password = "wrong" };

        for (var i = 0; i < PermitLimit + 1; i++)
        {
            await client.PostAsJsonAsync("/api/auth/login", loginRequest);
        }

        // No Authorization header — /api/auth/validate answers 401 (not rate-limited),
        // proving the "auth-anon" partition did not spill over to an unrelated route.
        var validateResponse = await client.GetAsync("/api/auth/validate");

        validateResponse.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
    }

    /// <summary>
    /// The flip side of the previous test: "auth-anon" is ONE policy shared by every
    /// decorated endpoint (login, register, ares, forgot-password, set-password,
    /// validate-invitation, 2FA verify), partitioned by caller IP — not one independent
    /// counter per route. Starving the bucket via login must therefore also 429 register,
    /// for the SAME caller, even though register was never itself called enough times to
    /// trip its own count. Without this, an attacker could round-robin across the seven
    /// decorated endpoints and get PermitLimit attempts on EACH instead of PermitLimit total.
    /// </summary>
    [Fact]
    public async Task Login_ExceedsPermitLimit_AlsoRejectsRegister_SharedPartitionAcrossEndpoints()
    {
        using var factory = new TightRateLimitFactory();
        var client = factory.CreateClient();
        var loginRequest = new LoginRequest { Email = "nobody3@example.com", Password = "wrong" };

        for (var i = 0; i < PermitLimit; i++)
        {
            await client.PostAsJsonAsync("/api/auth/login", loginRequest);
        }

        // The bucket is now exactly at its limit — register on the same (test-host) IP must
        // be rejected on its very FIRST call, never having been counted against itself.
        var registerResponse = await client.PostAsJsonAsync("/api/auth/register", new { });

        registerResponse.StatusCode.ShouldBe(HttpStatusCode.TooManyRequests);
    }

    /// <summary>
    /// RC.4's partition key is the caller's IP — after <c>UseForwardedHeaders</c> with
    /// <c>ForwardLimit = 1</c> resolves it from X-Forwarded-For. Two callers behind different
    /// forwarded IPs must get independent buckets: proves the partition key is actually
    /// per-IP and not one process-wide bucket in disguise.
    /// </summary>
    [Fact]
    public async Task Login_TwoDifferentForwardedIps_GetIndependentRateLimitBuckets()
    {
        using var factory = new TightRateLimitFactory();
        var client = factory.CreateClient();
        var loginRequest = new LoginRequest { Email = "nobody4@example.com", Password = "wrong" };

        for (var i = 0; i < PermitLimit; i++)
        {
            await SendLoginAsync(client, loginRequest, forwardedFor: "203.0.113.10");
        }

        // A fresh IP must still have its full, untouched quota — the previous IP's exhausted
        // bucket must not have been shared.
        var freshIpResponse = await SendLoginAsync(client, loginRequest, forwardedFor: "203.0.113.20");

        freshIpResponse.StatusCode.ShouldBe(HttpStatusCode.Unauthorized,
            "a different forwarded IP must have its own independent bucket");
    }

    /// <summary>
    /// <c>ForwardLimit = 1</c> means only the RIGHT-MOST X-Forwarded-For entry is trusted —
    /// the one Azure's own edge appends — because everything to its LEFT was supplied by the
    /// client and is trivially spoofable. Without this limit, an attacker could prepend a
    /// fresh random IP to X-Forwarded-For on every request and get a brand-new rate-limit
    /// bucket each time, defeating RC.4 entirely. This test proves that changing only the
    /// spoofable left-most entry, while keeping the trusted right-most one fixed, still hits
    /// ONE shared bucket.
    /// </summary>
    [Fact]
    public async Task Login_SpoofedLeftmostForwardedForEntry_DoesNotBypassRateLimit()
    {
        using var factory = new TightRateLimitFactory();
        var client = factory.CreateClient();
        var loginRequest = new LoginRequest { Email = "nobody5@example.com", Password = "wrong" };
        const string realEdgeAppendedIp = "203.0.113.30";

        for (var i = 0; i < PermitLimit; i++)
        {
            // A different forged left-most entry on every request, as an attacker trying to
            // dodge the limiter would send — but the real, right-most entry never changes.
            var spoofedChain = $"198.51.100.{i}, {realEdgeAppendedIp}";
            var response = await SendLoginAsync(client, loginRequest, forwardedFor: spoofedChain);
            response.StatusCode.ShouldBe(HttpStatusCode.Unauthorized,
                $"request #{i + 1} of {PermitLimit} is within the permit limit");
        }

        var rejected = await SendLoginAsync(
            client, loginRequest, forwardedFor: $"198.51.100.999, {realEdgeAppendedIp}");

        rejected.StatusCode.ShouldBe(HttpStatusCode.TooManyRequests,
            "spoofing only the left-most (client-supplied) hop must not grant a fresh bucket");
    }

    private static async Task<HttpResponseMessage> SendLoginAsync(
        HttpClient client, LoginRequest loginRequest, string forwardedFor)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/auth/login")
        {
            Content = JsonContent.Create(loginRequest)
        };
        request.Headers.Add("X-Forwarded-For", forwardedFor);
        return await client.SendAsync(request);
    }

    /// <summary>
    /// Same test host as everything else (real routing/auth pipeline, InMemory database —
    /// see FakvioFactory), but with the auth-anon limiter dialed back down to a size a
    /// unit test can actually exhaust in a handful of requests.
    /// </summary>
    public class TightRateLimitFactory : FakvioFactory
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            base.ConfigureWebHost(builder);

            // Overrides the "1000000" the base factory sets — UseSetting applied later
            // wins, same mechanism InvoiceApiFactory itself relies on.
            builder.UseSetting("RateLimiting:AuthAnon:PermitLimit", PermitLimit.ToString());
            builder.UseSetting("RateLimiting:AuthAnon:WindowSeconds", "60");
        }
    }
}

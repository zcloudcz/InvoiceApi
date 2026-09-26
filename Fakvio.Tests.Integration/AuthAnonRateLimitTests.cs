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

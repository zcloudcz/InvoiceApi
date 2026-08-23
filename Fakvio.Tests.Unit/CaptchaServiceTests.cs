using System.Net;
using Fakvio.Infrastructure.Service;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using NSubstitute;
using Shouldly;

namespace Fakvio.Tests.Unit;

/// <summary>
/// Tests for the reCAPTCHA v3 gate (<see cref="CaptchaService"/>) — issue #200.
///
/// WHY THIS FILE EXISTS
///   The gate is the only abuse protection in front of three anonymous endpoints
///   (/api/auth/login, /api/auth/register and the ARES proxy /api/auth/ares/{ico}).
///   The repo has no rate limiting anywhere, so if the gate lets a request through
///   when it should not, nothing else stops it.
///
///   Three defects were fixed here (all found in review of PR #173):
///     1. a failed verification (exception, HTTP error, missing secret key) used to
///        return TRUE — an outage at Google switched the gate off completely,
///     2. the "action" the token was issued for was never compared, so a token minted
///        on the public registration form worked on any other gated endpoint,
///     3. the "hostname" the token was issued on was never compared either.
///
///   The escape hatch for local development is now EXPLICIT config
///   ("Recaptcha:Enabled": false) instead of "no secret key = no gate", which used to
///   silently disable the gate in any environment where the key was simply forgotten.
///
/// Google's siteverify endpoint is stubbed — these tests never touch the network.
/// </summary>
public class CaptchaServiceTests
{
    private const string SecretKey = "test-secret-key";
    private const string ExpectedAction = "login";
    private const string AllowedHostname = "fakvio.cz";
    private const string Token = "token-from-grecaptcha-execute";

    // ── Fail closed ───────────────────────────────────────────────────────────

    /// <summary>
    /// THE BUG: the catch block used to return true ("fail open — rate limiting still
    /// protects us"), and there is no rate limiting in this repo. Any transport error
    /// towards Google therefore opened all three anonymous endpoints.
    /// </summary>
    [Fact]
    public async Task Verify_TransportFailure_RejectsInsteadOfFailingOpen()
    {
        var service = CreateService(
            new ThrowingHttpMessageHandler(new HttpRequestException("google unreachable")));

        var result = await service.VerifyAsync(Token, ExpectedAction);

        result.ShouldBeFalse("an outage at Google must not switch the captcha gate off");
    }

    /// <summary>
    /// THE BUG: an empty secret key used to mean "skip verification" in EVERY
    /// environment. Production reads the key from Azure App Settings, so a forgotten
    /// setting silently removed the gate. Skipping is now opt-in via Recaptcha:Enabled.
    /// </summary>
    [Fact]
    public async Task Verify_EnabledButSecretKeyMissing_RejectsInsteadOfSkipping()
    {
        var service = CreateService(
            new StubHttpMessageHandler(SiteVerifyOk()),
            Settings(secretKey: ""));

        var result = await service.VerifyAsync(Token, ExpectedAction);

        result.ShouldBeFalse("a misconfigured gate is a closed gate, not an absent one");
    }

    [Fact]
    public async Task Verify_GoogleReturnsHttpError_Rejects()
    {
        var service = CreateService(
            new StubHttpMessageHandler(new HttpResponseMessage(HttpStatusCode.ServiceUnavailable)));

        var result = await service.VerifyAsync(Token, ExpectedAction);

        result.ShouldBeFalse();
    }

    // ── Explicit local-development escape hatch ───────────────────────────────

    /// <summary>
    /// Local development and the integration test host have no reCAPTCHA keys. They
    /// set "Recaptcha:Enabled": false, which skips verification without any outbound
    /// call — the ThrowingHttpMessageHandler proves nothing is sent to Google.
    /// </summary>
    [Fact]
    public async Task Verify_ExplicitlyDisabled_PassesWithoutCallingGoogle()
    {
        var service = CreateService(
            new ThrowingHttpMessageHandler(new InvalidOperationException("must not be called")),
            Settings(enabled: "false", secretKey: ""));

        var result = await service.VerifyAsync(Token, ExpectedAction);

        result.ShouldBeTrue("the dev/test escape hatch has to keep working");
    }

    // ── Action binding ────────────────────────────────────────────────────────

    /// <summary>
    /// THE BUG: reCAPTCHA v3 tokens carry the action they were issued for, and the
    /// service never looked at it. A token minted by the public registration page
    /// ("register") was accepted by the anonymous ARES proxy, which expects "ares".
    /// </summary>
    [Fact]
    public async Task Verify_TokenIssuedForAnotherAction_Rejects()
    {
        var service = CreateService(
            new StubHttpMessageHandler(SiteVerifyOk(action: "register")));

        var result = await service.VerifyAsync(Token, expectedAction: "ares");

        result.ShouldBeFalse("a token for one endpoint must not be replayable on another");
    }

    [Fact]
    public async Task Verify_ActionMatches_Passes()
    {
        var service = CreateService(new StubHttpMessageHandler(SiteVerifyOk(action: "ares")));

        var result = await service.VerifyAsync(Token, expectedAction: "ares");

        result.ShouldBeTrue();
    }

    // ── Hostname binding ──────────────────────────────────────────────────────

    /// <summary>
    /// THE BUG: the hostname the token was issued on was never compared, so a token
    /// obtained on an attacker-controlled page using our site key passed here.
    /// </summary>
    [Fact]
    public async Task Verify_TokenIssuedOnForeignHost_Rejects()
    {
        var service = CreateService(
            new StubHttpMessageHandler(SiteVerifyOk(hostname: "evil.example.com")));

        var result = await service.VerifyAsync(Token, ExpectedAction);

        result.ShouldBeFalse("the token must come from one of our own hosts");
    }

    [Fact]
    public async Task Verify_HostnameComparisonIgnoresCase()
    {
        var service = CreateService(
            new StubHttpMessageHandler(SiteVerifyOk(hostname: "FAKVIO.CZ")));

        var result = await service.VerifyAsync(Token, ExpectedAction);

        result.ShouldBeTrue("DNS names are case-insensitive");
    }

    /// <summary>
    /// Documented tradeoff: when no hostname is configured the check is skipped, because
    /// the reCAPTCHA admin console already binds the site key to a domain list. The rest
    /// of the verification (success, score, action) still applies — this is not the old
    /// fail-open path. See the PR for issue #200.
    /// </summary>
    [Fact]
    public async Task Verify_NoAllowedHostnamesConfigured_SkipsOnlyTheHostnameCheck()
    {
        var settings = Settings();
        settings.Remove("Recaptcha:AllowedHostnames:0");

        var service = CreateService(
            new StubHttpMessageHandler(SiteVerifyOk(hostname: "anything.example.com")),
            settings);

        var result = await service.VerifyAsync(Token, ExpectedAction);

        result.ShouldBeTrue();
    }

    // ── Pre-existing rules that must keep working ─────────────────────────────

    [Fact]
    public async Task Verify_EmptyToken_Rejects()
    {
        var service = CreateService(
            new ThrowingHttpMessageHandler(new InvalidOperationException("must not be called")));

        var result = await service.VerifyAsync("", ExpectedAction);

        result.ShouldBeFalse();
    }

    [Fact]
    public async Task Verify_ScoreBelowThreshold_Rejects()
    {
        var service = CreateService(new StubHttpMessageHandler(SiteVerifyOk(score: 0.1)));

        var result = await service.VerifyAsync(Token, ExpectedAction);

        result.ShouldBeFalse();
    }

    [Fact]
    public async Task Verify_GoogleReportsFailure_Rejects()
    {
        var service = CreateService(new StubHttpMessageHandler(
            Json("""{"success":false,"score":0.0,"action":"login","hostname":"fakvio.cz"}""")));

        var result = await service.VerifyAsync(Token, ExpectedAction);

        result.ShouldBeFalse();
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    /// <summary>
    /// Default configuration: gate enabled, secret key present, one allowed hostname.
    /// Individual tests override what they are about.
    /// </summary>
    private static Dictionary<string, string?> Settings(
        string? enabled = "true",
        string? secretKey = SecretKey) => new()
    {
        ["Recaptcha:Enabled"] = enabled,
        ["Recaptcha:SecretKey"] = secretKey,
        ["Recaptcha:AllowedHostnames:0"] = AllowedHostname
    };

    private static CaptchaService CreateService(
        HttpMessageHandler handler,
        Dictionary<string, string?>? settings = null)
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(settings ?? Settings())
            .Build();

        return new CaptchaService(
            new HttpClient(handler),
            configuration,
            Substitute.For<ILogger<CaptchaService>>());
    }

    /// <summary>A successful siteverify payload, shaped exactly like Google's.</summary>
    private static HttpResponseMessage SiteVerifyOk(
        double score = 0.9,
        string action = ExpectedAction,
        string hostname = AllowedHostname)
        => Json($$"""{"success":true,"score":{{score.ToString(System.Globalization.CultureInfo.InvariantCulture)}},"action":"{{action}}","hostname":"{{hostname}}"}""");

    private static HttpResponseMessage Json(string body) => new(HttpStatusCode.OK)
    {
        Content = new StringContent(body, System.Text.Encoding.UTF8, "application/json")
    };

    /// <summary>HttpMessageHandler stub returning a fixed response for any request.</summary>
    private sealed class StubHttpMessageHandler(HttpResponseMessage response) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
            => Task.FromResult(response);
    }

    /// <summary>HttpMessageHandler stub throwing the given exception on every request.</summary>
    private sealed class ThrowingHttpMessageHandler(Exception exception) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
            => Task.FromException<HttpResponseMessage>(exception);
    }
}

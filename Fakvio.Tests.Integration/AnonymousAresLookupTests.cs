using System.Net;
using System.Net.Http.Json;
using AresService;
using AresService.Model;
using Fakvio.Contracts.Dto.Auth;
using Fakvio.Infrastructure.Service;
using Fakvio.Tests.Integration.Fixtures;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using NSubstitute;
using NSubstitute.ClearExtensions;
using NSubstitute.ExceptionExtensions;
using Shouldly;

namespace Fakvio.Tests.Integration;

/// <summary>
/// Regression tests for the second half of issue #157.
///
/// THE BUG (found in review round 1 of PR #173):
///   /register is an anonymous page, but its "Load from ARES" button called
///   GET /api/client/ares/{ico}. ClientController is [Authorize] at class level, so the
///   visitor — who has no account and therefore no JWT — always got 401. The Azure
///   Functions host had the same guard generated into ClientFunctions.Client_FetchFromAres.
///   Result: the company name never pre-filled, the registered office section never
///   populated, and acceptance criterion #2 of the issue could not be met.
///
/// THE FIX:
///   A dedicated anonymous endpoint GET /api/auth/ares/{ico} on AuthController, guarded
///   by the same reCAPTCHA v3 gate as login/register and returning only the narrow
///   AresLookupResponse (name + registered office). ClientController stays authenticated.
///
/// WHAT THESE TESTS PIN:
///   - the anonymous endpoint really is reachable without a token (the actual bug),
///   - it returns the registered office the form needs,
///   - the tenant-scoped ClientController endpoint stays behind [Authorize],
///   - malformed input never reaches the public ARES registry,
///   - the reCAPTCHA gate is really in front of the endpoint, and the token is verified
///     against the "ares" action (issue #200),
///   - no registry or exception detail is ever echoed to the anonymous caller.
///
/// The registry itself is substituted — these tests must not depend on the network.
/// The live-registry contract is covered separately by AresAddressBoundaryTests, and the
/// failure shapes fed in below are pinned to the real ones by AresLookupErrorSurfaceTests.
/// </summary>
public class AnonymousAresLookupTests : IClassFixture<AnonymousAresLookupTests.AresStubFactory>
{
    private readonly AresStubFactory _factory;

    // Distinct IČOs per test — the substitutes are shared by the class fixture, so
    // configuring different numbers keeps the tests independent of execution order.
    private const string FoundIco = "11111111";
    private const string NotFoundIco = "22222222";
    private const string CaptchaBlockedIco = "33333333";
    private const string ErrorShapeIco = "44444444";
    private const string ThrowingIco = "55555555";
    private const string MalformedIco = "12ab";

    // Arabic-Indic digits — eight characters that char.IsDigit accepts. See the
    // known-gap test at the bottom of this file.
    private const string NonAsciiDigitsIco = "١٢٣٤٥٦٧٨";

    // Stand-in for anything an anonymous caller must never learn: an internal host,
    // a connection string, a stack frame. Embedded in the simulated failures below.
    private const string InternalDetail = "10.0.0.7:5432 refused";

    // The two fixed texts AuthController.FetchFromAres is allowed to return on failure.
    private const string GenericLookupFailureBody = """{"message":"Company not found in ARES."}""";
    private const string GenericServerErrorBody = """{"message":"An error occurred during the ARES lookup."}""";
    private const string CaptchaFailureBody = """{"message":"CAPTCHA verification failed. Please try again."}""";
    private const string MalformedInputBody = """{"message":"Registration number must be exactly 8 digits."}""";

    public AnonymousAresLookupTests(AresStubFactory factory)
    {
        _factory = factory;

        // xUnit builds the test class per test, so this runs before every test: the
        // captcha starts out permissive and with an empty call log, whatever the
        // previous test in this class did to it.
        _factory.Captcha.ClearSubstitute();
        _factory.Captcha.VerifyAsync(Arg.Any<string?>(), Arg.Any<string>()).Returns(true);
    }

    /// <summary>
    /// The core regression test: an anonymous caller (a visitor of /register) must get
    /// the company data, not 401. Before the fix this route did not exist at all.
    /// </summary>
    [Fact]
    public async Task AuthAresEndpoint_AnonymousCaller_ReturnsCompanyWithRegisteredOffice()
    {
        // Arrange — no Authorization header is set anywhere in this test.
        _factory.Ares
            .GetCompanyInfoAsync(FoundIco, Arg.Any<CancellationToken>())
            .Returns(new AresCompanyInfo
            {
                IsSuccessful = true,
                RegistrationNumber = FoundIco,
                CompanyName = "Testovací firma s.r.o.",
                Address = new AresAddress
                {
                    Street = "Hlavní 123",
                    City = "Praha",
                    PostalCode = "120 00",
                    Country = "Česká republika"
                }
            });

        var client = _factory.CreateClient();

        // Act
        var response = await client.GetAsync($"/api/auth/ares/{FoundIco}");

        // Assert
        response.StatusCode.ShouldBe(HttpStatusCode.OK,
            "the registration form calls this endpoint without a token");

        var payload = await response.Content.ReadFromJsonAsync<AresLookupResponse>();
        payload.ShouldNotBeNull();
        payload!.CompanyName.ShouldBe("Testovací firma s.r.o.");
        payload.Street.ShouldBe("Hlavní 123");
        payload.City.ShouldBe("Praha");
        payload.PostalCode.ShouldBe("120 00");
        payload.Country.ShouldBe("Česká republika");
    }

    /// <summary>
    /// The tenant-scoped proxy must NOT be opened up as a side effect of the fix.
    /// If someone points the registration form back at it, this test fails first.
    /// </summary>
    [Fact]
    public async Task ClientAresEndpoint_AnonymousCaller_StaysUnauthorized()
    {
        var client = _factory.CreateClient();

        var response = await client.GetAsync($"/api/client/ares/{FoundIco}");

        response.StatusCode.ShouldBe(HttpStatusCode.Unauthorized,
            "ClientController is tenant-scoped and must keep requiring a JWT");
    }

    /// <summary>
    /// Fail fast at the boundary: garbage input is rejected before any outbound call,
    /// so the anonymous endpoint cannot be used to hammer the public ARES registry.
    /// </summary>
    [Fact]
    public async Task AuthAresEndpoint_MalformedIco_ReturnsBadRequestWithoutCallingRegistry()
    {
        var client = _factory.CreateClient();

        var response = await client.GetAsync($"/api/auth/ares/{MalformedIco}");

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        await _factory.Ares.DidNotReceive()
            .GetCompanyInfoAsync(MalformedIco, Arg.Any<CancellationToken>());
    }

    /// <summary>
    /// An unknown IČO is a normal outcome — the form shows "failed to load", so a 4xx
    /// (not a 500) is the right answer.
    /// </summary>
    [Fact]
    public async Task AuthAresEndpoint_CompanyNotFound_ReturnsBadRequest()
    {
        _factory.Ares
            .GetCompanyInfoAsync(NotFoundIco, Arg.Any<CancellationToken>())
            .Returns(new AresCompanyInfo
            {
                IsSuccessful = false,
                ErrorMessage = "Company not found in registry (HTTP NotFound)"
            });

        var client = _factory.CreateClient();

        var response = await client.GetAsync($"/api/auth/ares/{NotFoundIco}");

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
    }

    // ── The security fix: AresCompanyInfo.ErrorMessage never reaches the caller ──

    /// <summary>
    /// The most important assertion in this file. AresServiceImpl builds several of its
    /// ErrorMessage values from raw exception text, and this endpoint answers strangers.
    /// Returning info.ErrorMessage verbatim — which an earlier draft of the endpoint did —
    /// would describe our infrastructure to anyone who can hit /register.
    ///
    /// Every failure shape the service can produce is listed here; the map is pinned on
    /// the producing side by AresLookupErrorSurfaceTests, so the two files stay in step.
    /// The assertion is on the RAW body, not on a parsed DTO: a leak added as an extra
    /// JSON property would slip past a typed deserialization.
    /// </summary>
    [Theory]
    // AresServiceImpl.GetCompanyInfoAsync — input rejected before the registry call
    [InlineData("Registration number must be exactly 8 digits", "8 digits")]
    // AresServiceImpl.RefreshCompanyInfoAsync — registry answered with a non-2xx status
    [InlineData("Company not found in registry (HTTP NotFound)", "HTTP NotFound")]
    // AresServiceImpl.RefreshCompanyInfoAsync — HttpRequestException, text from the exception
    [InlineData("ARES API connection error: " + InternalDetail, InternalDetail)]
    // AresServiceImpl.RefreshCompanyInfoAsync — catch-all, text from the exception
    [InlineData("Unexpected error: " + InternalDetail, InternalDetail)]
    // AresServiceImpl.ParseAresResponse — malformed registry payload, text from the exception
    [InlineData("Error parsing ARES response: " + InternalDetail, InternalDetail)]
    // AresServiceImpl.DeserializeCachedData — cached row did not deserialize
    [InlineData("Failed to deserialize cached data", "deserialize")]
    // AresServiceImpl.DeserializeCachedData — cached row threw, text from the exception.
    // This one arrives through the cache, so it can be replayed on every later request.
    [InlineData("Error reading cached data: " + InternalDetail, InternalDetail)]
    public async Task AuthAresEndpoint_LookupFailed_AnswersGenericallyWithoutRegistryDetail(
        string registryErrorMessage, string mustNotAppearInBody)
    {
        _factory.Ares
            .GetCompanyInfoAsync(ErrorShapeIco, Arg.Any<CancellationToken>())
            .Returns(new AresCompanyInfo
            {
                RegistrationNumber = ErrorShapeIco,
                IsSuccessful = false,
                ErrorMessage = registryErrorMessage
            });

        var client = _factory.CreateClient();

        var response = await client.GetAsync($"/api/auth/ares/{ErrorShapeIco}");
        var body = await response.Content.ReadAsStringAsync();

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        body.ShouldNotContain(mustNotAppearInBody, Case.Insensitive,
            "ErrorMessage must stay in the log, not in the response to an anonymous caller");
        body.ShouldBe(GenericLookupFailureBody,
            "every failure shape collapses to one fixed message");
    }

    /// <summary>
    /// The catch-all branch. An exception escaping the registry client must become a bare
    /// 500 — not an exception message, and not a stack trace.
    /// </summary>
    [Fact]
    public async Task AuthAresEndpoint_RegistryThrows_ReturnsGenericServerErrorWithoutExceptionDetail()
    {
        _factory.Ares
            .GetCompanyInfoAsync(ThrowingIco, Arg.Any<CancellationToken>())
            .Throws(new InvalidOperationException(InternalDetail));

        var client = _factory.CreateClient();

        var response = await client.GetAsync($"/api/auth/ares/{ThrowingIco}");
        var body = await response.Content.ReadAsStringAsync();

        response.StatusCode.ShouldBe(HttpStatusCode.InternalServerError);
        body.ShouldNotContain(InternalDetail, Case.Insensitive);
        body.ShouldNotContain(nameof(InvalidOperationException), Case.Insensitive);
        body.ShouldBe(GenericServerErrorBody);
    }

    // ── The reCAPTCHA gate in front of the anonymous endpoint ────────────────

    /// <summary>
    /// The gate is the only abuse protection this endpoint has (the repo has no rate
    /// limiting anywhere). Without this test the endpoint would still look correct with
    /// the captcha call deleted, because the test configuration has no reCAPTCHA secret
    /// and the real service then skips verification.
    /// </summary>
    [Fact]
    public async Task AuthAresEndpoint_CaptchaRejectsToken_ReturnsBadRequestWithoutCallingRegistry()
    {
        _factory.Captcha.VerifyAsync(Arg.Any<string?>(), Arg.Any<string>()).Returns(false);

        var client = _factory.CreateClient();

        var response = await client.GetAsync($"/api/auth/ares/{CaptchaBlockedIco}");
        var body = await response.Content.ReadAsStringAsync();

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        body.ShouldBe(CaptchaFailureBody);
        await _factory.Ares.DidNotReceive()
            .GetCompanyInfoAsync(CaptchaBlockedIco, Arg.Any<CancellationToken>());
    }

    /// <summary>
    /// The token the browser sends must be the one that gets verified. A gate that always
    /// verifies null would pass the test above and still be useless in production.
    ///
    /// The expected action is pinned too (issue #200): the token must have been issued for
    /// "ares" — the same string Register.razor passes to grecaptcha.execute() — otherwise a
    /// token minted on the registration form itself would open the registry proxy.
    /// </summary>
    [Fact]
    public async Task AuthAresEndpoint_ForwardsCaptchaTokenHeaderAndAresActionToVerifier()
    {
        const string browserToken = "token-from-grecaptcha-execute";

        _factory.Ares
            .GetCompanyInfoAsync(FoundIco, Arg.Any<CancellationToken>())
            .Returns(new AresCompanyInfo { IsSuccessful = true, RegistrationNumber = FoundIco });

        var client = _factory.CreateClient();
        var request = new HttpRequestMessage(HttpMethod.Get, $"/api/auth/ares/{FoundIco}");
        request.Headers.Add("X-Captcha-Token", browserToken);

        await client.SendAsync(request);

        await _factory.Captcha.Received(1).VerifyAsync(browserToken, "ares");
    }

    // ── The former known gap, now closed (issue #200) ────────────────────────

    /// <summary>
    /// THE BUG: IsValidRegistrationNumber used char.IsDigit, which is true for every
    /// Unicode decimal digit, not just '0'-'9'. Eight Arabic-Indic digits therefore passed
    /// validation and reached the outbound registry call, contradicting the endpoint's own
    /// doc comment ("a malformed IČO never leaves our host") and widening the key space of
    /// the shared AresCache well beyond 10^8 — which matters now that the endpoint is
    /// anonymous and the cache is the thing being grown.
    ///
    /// This test used to assert the opposite (characterizing the gap); it was flipped when
    /// the guard was narrowed to ASCII digits.
    /// </summary>
    [Fact]
    public async Task AuthAresEndpoint_NonAsciiDigits_RejectedBeforeReachingTheRegistry()
    {
        var client = _factory.CreateClient();

        var response = await client.GetAsync($"/api/auth/ares/{NonAsciiDigitsIco}");
        var body = await response.Content.ReadAsStringAsync();

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        body.ShouldBe(MalformedInputBody, "the guard rejects anything that is not ASCII 0-9");
        await _factory.Ares.DidNotReceive()
            .GetCompanyInfoAsync(NonAsciiDigitsIco, Arg.Any<CancellationToken>());
    }

    /// <summary>
    /// Test host with the ARES registry and the reCAPTCHA verifier replaced by
    /// substitutes — no network access. Everything else (routing, authentication,
    /// authorization) is the real pipeline, which is exactly what these tests are about.
    /// </summary>
    public class AresStubFactory : FakvioFactory
    {
        /// <summary>The substituted registry — configure it per test.</summary>
        public IAresService Ares { get; } = Substitute.For<IAresService>();

        /// <summary>
        /// The substituted captcha gate. The test host sets "Recaptcha:Enabled": false
        /// (see FakvioFactory), so the real CaptchaService would return true for every
        /// token — substituting it is the only way to tell "the gate passed me" from
        /// "the gate is not there".
        /// Reset by the test class constructor before every test.
        /// </summary>
        public ICaptchaService Captcha { get; } = Substitute.For<ICaptchaService>();

        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            base.ConfigureWebHost(builder);

            builder.ConfigureServices(services =>
            {
                // AddHttpClient<IAresService, AresServiceImpl>() registers a typed client;
                // RemoveAll drops it so no real HTTP call can ever leave the test host.
                services.RemoveAll<IAresService>();
                services.AddSingleton(Ares);

                // Same story for ICaptchaService — a typed client pointed at Google.
                services.RemoveAll<ICaptchaService>();
                services.AddSingleton(Captcha);
            });
        }
    }
}

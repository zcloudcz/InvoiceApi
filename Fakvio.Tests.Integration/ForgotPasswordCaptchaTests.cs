using System.Net;
using System.Net.Http.Json;
using Fakvio.Application.Service;
using Fakvio.Contracts.Dto.User;
using Fakvio.Infrastructure.Service;
using Fakvio.Tests.Integration.Fixtures;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using NSubstitute;
using NSubstitute.ClearExtensions;
using Shouldly;

namespace Fakvio.Tests.Integration;

/// <summary>
/// RC.3 — reCAPTCHA gate in front of POST /api/user/forgot-password.
///
/// THE GAP: unlike login/register/ares (AuthController), forgot-password had no bot
/// protection at all. Anyone could hammer it with arbitrary addresses to spam inboxes
/// (email bombing) at no cost — the endpoint always answers 200, by design, so a
/// missing gate is invisible from the response alone.
///
/// THE FIX: same pattern as AuthController — X-Captcha-Token header, action
/// "forgot_password", verified via ICaptchaService BEFORE anything email-specific runs.
///
/// WHAT THESE TESTS PIN:
///   - a request without a captcha token never reaches IUserService (the actual gate),
///   - the token is verified against the "forgot_password" action specifically,
///   - the anti-enumeration guarantee is untouched: a CAPTCHA pass gets 200 whether or
///     not the email exists, and the body never differs.
///
/// IUserService/IEmailService are substituted — no real email is ever sent, and no
/// database write is needed to pin the gate itself.
/// </summary>
public class ForgotPasswordCaptchaTests : IClassFixture<ForgotPasswordCaptchaTests.ForgotPasswordStubFactory>
{
    private readonly ForgotPasswordStubFactory _factory;

    private const string ExistingEmail = "existing@example.com";
    private const string UnknownEmail = "unknown@example.com";
    private const string CaptchaFailureBody = """{"message":"CAPTCHA verification failed. Please try again."}""";

    public ForgotPasswordCaptchaTests(ForgotPasswordStubFactory factory)
    {
        _factory = factory;

        // Reset before every test — xUnit builds the test class per test, but the
        // substitutes themselves are shared by the class fixture.
        _factory.Captcha.ClearSubstitute();
        _factory.UserService.ClearSubstitute();
        _factory.Captcha.VerifyAsync(Arg.Any<string?>(), Arg.Any<string>()).Returns(true);
        _factory.UserService
            .ForgotPasswordAsync(ExistingEmail, Arg.Any<CancellationToken>())
            .Returns("reset-token-123");
        _factory.UserService
            .ForgotPasswordAsync(UnknownEmail, Arg.Any<CancellationToken>())
            .Returns((string?)null);
    }

    [Fact]
    public async Task ForgotPassword_NoCaptchaToken_ReturnsBadRequest_WithoutCallingUserService()
    {
        _factory.Captcha.VerifyAsync(Arg.Any<string?>(), Arg.Any<string>()).Returns(false);

        var client = _factory.CreateClient();

        var response = await client.PostAsJsonAsync("/api/user/forgot-password",
            new ForgotPasswordDto { Email = ExistingEmail });
        var body = await response.Content.ReadAsStringAsync();

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        body.ShouldBe(CaptchaFailureBody);
        await _factory.UserService.DidNotReceive()
            .ForgotPasswordAsync(Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ForgotPassword_ForwardsCaptchaTokenHeaderAndForgotPasswordActionToVerifier()
    {
        const string browserToken = "token-from-grecaptcha-execute";

        var client = _factory.CreateClient();
        var request = new HttpRequestMessage(HttpMethod.Post, "/api/user/forgot-password")
        {
            Content = JsonContent.Create(new ForgotPasswordDto { Email = ExistingEmail })
        };
        request.Headers.Add("X-Captcha-Token", browserToken);

        await client.SendAsync(request);

        await _factory.Captcha.Received(1).VerifyAsync(browserToken, "forgot_password");
    }

    /// <summary>
    /// The anti-enumeration guarantee (DEVGUIDE §2.5) must survive the new gate: once the
    /// CAPTCHA passes, an existing and a non-existing email get the exact same response.
    /// </summary>
    [Theory]
    [InlineData(ExistingEmail)]
    [InlineData(UnknownEmail)]
    public async Task ForgotPassword_CaptchaPasses_AlwaysReturnsOkWithSameGenericBody(string email)
    {
        var client = _factory.CreateClient();

        var response = await client.PostAsJsonAsync("/api/user/forgot-password",
            new ForgotPasswordDto { Email = email });
        var body = await response.Content.ReadAsStringAsync();

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        body.ShouldBe("""{"message":"If the email exists, a password reset link has been sent."}""");
    }

    /// <summary>
    /// Test host with IUserService and ICaptchaService replaced by substitutes — no
    /// database, no outbound email, no network. Everything else (routing, model binding,
    /// the [AllowAnonymous] pipeline) is the real thing.
    /// </summary>
    public class ForgotPasswordStubFactory : FakvioFactory
    {
        public IUserService UserService { get; } = Substitute.For<IUserService>();
        public ICaptchaService Captcha { get; } = Substitute.For<ICaptchaService>();

        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            base.ConfigureWebHost(builder);

            builder.ConfigureServices(services =>
            {
                services.RemoveAll<IUserService>();
                services.AddSingleton(UserService);

                services.RemoveAll<ICaptchaService>();
                services.AddSingleton(Captcha);
            });
        }
    }
}

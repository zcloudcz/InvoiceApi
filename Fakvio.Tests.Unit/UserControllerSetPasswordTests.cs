// ============================================================================
// UserControllerSetPasswordTests — coverage for PR #175 (issue #152).
//
// UserInvitationTests covers the service layer (what SetPasswordResultDto says).
// These tests cover the HTTP hop right above it, which is where the old bug
// used to disappear: the controller must NOT turn "workspace not provisioned"
// into an error status. It is a successful password change with a warning flag,
// and the flag has to survive onto the wire — otherwise the client cannot tell
// the difference and falls back to reporting plain success.
// ============================================================================

using Fakvio.API.Controller;
using Fakvio.Application.Service;
using Fakvio.Contracts.Dto.User;
using Fakvio.Infrastructure.Service;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using NSubstitute.ExceptionExtensions;
using Shouldly;

namespace Fakvio.Tests.Unit;

/// <summary>
/// Tests for <see cref="UserController.SetPassword"/> — the anonymous endpoint used by
/// the invitation / password-reset link. Pure controller-layer tests: the service is
/// substituted, so each test states exactly one service outcome and asserts the HTTP shape.
/// </summary>
public class UserControllerSetPasswordTests
{
    private const string ValidToken = "valid-invitation-token";

    private readonly IUserService _userService = Substitute.For<IUserService>();

    /// <summary>
    /// Builds the controller under test. Only IUserService matters for this endpoint —
    /// the remaining constructor dependencies are inert substitutes.
    /// </summary>
    private UserController BuildController()
        => new(
            _userService,
            Substitute.For<IEmailService>(),
            Substitute.For<ISystemConfigurationService>(),
            Substitute.For<ICaptchaService>(),
            Substitute.For<IConfiguration>(),
            NullLogger<UserController>.Instance);

    /// <summary>
    /// Makes the substituted service answer with the given outcome for any set-password call.
    /// </summary>
    private void ServiceReturns(bool passwordSet, bool workspaceReady)
        => _userService
            .SetPasswordAsync(Arg.Any<SetPasswordDto>(), Arg.Any<CancellationToken>())
            .Returns(new SetPasswordResultDto { PasswordSet = passwordSet, WorkspaceReady = workspaceReady });

    private static SetPasswordDto RequestWith(string token)
        => new() { Token = token, NewPassword = "MySecurePassword123" };

    [Fact]
    public async Task SetPassword_WorkspaceProvisioned_Returns200_WithWorkspaceReady()
    {
        ServiceReturns(passwordSet: true, workspaceReady: true);

        var response = await BuildController().SetPassword(RequestWith(ValidToken));

        var ok = response.Result.ShouldBeOfType<OkObjectResult>();
        ok.StatusCode.ShouldBe(StatusCodes.Status200OK);
        ok.Value.ShouldBeOfType<SetPasswordResultDto>().WorkspaceReady.ShouldBeTrue();
    }

    /// <summary>
    /// Regression test for issue #152 at the HTTP boundary.
    /// A failed provisioning is not a failed password change — the endpoint answers 200 and
    /// carries WorkspaceReady = false, so the client can warn instead of celebrating.
    /// </summary>
    [Fact]
    public async Task SetPassword_ProvisioningFailed_Returns200_WithWorkspaceNotReady()
    {
        ServiceReturns(passwordSet: true, workspaceReady: false);

        var response = await BuildController().SetPassword(RequestWith(ValidToken));

        var ok = response.Result.ShouldBeOfType<OkObjectResult>();
        var body = ok.Value.ShouldBeOfType<SetPasswordResultDto>();

        body.PasswordSet.ShouldBeTrue();
        body.WorkspaceReady.ShouldBeFalse();
    }

    [Fact]
    public async Task SetPassword_InvalidOrExpiredToken_Returns400()
    {
        ServiceReturns(passwordSet: false, workspaceReady: false);

        var response = await BuildController().SetPassword(RequestWith("expired-or-unknown-token"));

        response.Result.ShouldBeOfType<BadRequestObjectResult>()
            .StatusCode.ShouldBe(StatusCodes.Status400BadRequest);
    }

    /// <summary>
    /// The endpoint is anonymous, so an unexpected failure must answer with a generic 500 —
    /// the exception text stays in the log and never reaches the caller.
    /// </summary>
    [Fact]
    public async Task SetPassword_ServiceThrows_Returns500_WithoutLeakingExceptionDetail()
    {
        const string sensitiveDetail = "Npgsql: password authentication failed for user 'fakvio_app'";

        _userService
            .SetPasswordAsync(Arg.Any<SetPasswordDto>(), Arg.Any<CancellationToken>())
            .ThrowsAsync(new InvalidOperationException(sensitiveDetail));

        var response = await BuildController().SetPassword(RequestWith(ValidToken));

        var error = response.Result.ShouldBeOfType<ObjectResult>();
        error.StatusCode.ShouldBe(StatusCodes.Status500InternalServerError);
        error.Value!.ToString().ShouldNotContain(sensitiveDetail);
    }
}

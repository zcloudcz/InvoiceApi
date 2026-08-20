// ============================================================================
// AdvanceTaxReceiptModeEndpointTests — HTTP-layer coverage for issue #145.
//
// Two hosts serve the same two endpoints and both must behave identically:
//
//   ASP.NET Core (Fakvio.API)      — ClientController actions, guarded by attributes
//   Azure Functions (Fakvio.Functions) — thin wrappers that must re-implement the
//                                        attribute guards by hand, because Functions
//                                        never evaluates MVC [Authorize].
//
// The role gate on the PUT is the security-relevant part: changing when advance
// invoices flip into tax receipts is a company-wide accounting decision, so a plain
// User must not be able to do it through either host.
// ============================================================================

using System.Reflection;
using System.Security.Claims;
using System.Text;
using System.Text.Json;
using Fakvio.API.Controller;
using Fakvio.Application.Service;
using Fakvio.Contracts.Dto.Client;
using Fakvio.Domain.Enums;
using Fakvio.Functions.Generated;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;
using NSubstitute;
using Shouldly;

namespace Fakvio.Tests.Unit;

/// <summary>
/// Controller-layer tests — the service is mocked, so these verify status codes,
/// delegation and the authorization attributes only.
/// </summary>
public class AdvanceTaxReceiptModeControllerTests
{
    private readonly IClientService _clientService = Substitute.For<IClientService>();

    private ClientController BuildController() =>
        new(_clientService, Substitute.For<ILogger<ClientController>>())
        {
            ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext() }
        };

    // ── GET ─────────────────────────────────────────────────────────────────

    [Fact]
    public async Task GetAdvanceTaxReceiptMode_ServiceReturnsMode_ReturnsOkWithValue()
    {
        _clientService.GetAdvanceTaxReceiptModeAsync(Arg.Any<CancellationToken>())
            .Returns(EAdvanceTaxReceiptMode.OnAnyPayment);

        var result = await BuildController().GetAdvanceTaxReceiptMode();

        var ok = result.Result.ShouldBeOfType<OkObjectResult>();
        ok.Value.ShouldBe(EAdvanceTaxReceiptMode.OnAnyPayment);
    }

    [Fact]
    public async Task GetAdvanceTaxReceiptMode_TenantHasNoIssuer_ReturnsNotFound()
    {
        _clientService.GetAdvanceTaxReceiptModeAsync(Arg.Any<CancellationToken>())
            .Returns((EAdvanceTaxReceiptMode?)null);

        var result = await BuildController().GetAdvanceTaxReceiptMode();

        result.Result.ShouldBeOfType<NotFoundObjectResult>();
    }

    // ── PUT ─────────────────────────────────────────────────────────────────

    [Fact]
    public async Task SetAdvanceTaxReceiptMode_ServiceSucceeds_ReturnsOkAndForwardsMode()
    {
        _clientService.SetAdvanceTaxReceiptModeAsync(
                Arg.Any<EAdvanceTaxReceiptMode>(), Arg.Any<CancellationToken>())
            .Returns(true);

        var result = await BuildController().SetAdvanceTaxReceiptMode(
            new SetAdvanceTaxReceiptModeDto { Mode = EAdvanceTaxReceiptMode.Disabled });

        var ok = result.Result.ShouldBeOfType<OkObjectResult>();
        ok.Value.ShouldBe(EAdvanceTaxReceiptMode.Disabled);
        // The controller must pass the body value through unchanged.
        await _clientService.Received(1).SetAdvanceTaxReceiptModeAsync(
            EAdvanceTaxReceiptMode.Disabled, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task SetAdvanceTaxReceiptMode_TenantHasNoIssuer_ReturnsNotFound()
    {
        _clientService.SetAdvanceTaxReceiptModeAsync(
                Arg.Any<EAdvanceTaxReceiptMode>(), Arg.Any<CancellationToken>())
            .Returns(false);

        var result = await BuildController().SetAdvanceTaxReceiptMode(
            new SetAdvanceTaxReceiptModeDto { Mode = EAdvanceTaxReceiptMode.OnAnyPayment });

        result.Result.ShouldBeOfType<NotFoundObjectResult>();
    }

    // ── Authorization attributes ────────────────────────────────────────────
    //
    // Attributes are not executed by unit tests, so assert their presence directly.
    // Losing the Roles filter would silently open a company-wide setting to every user.

    [Fact]
    public void SetAdvanceTaxReceiptMode_IsRestrictedToAdminAndSysAdmin()
    {
        var authorize = typeof(ClientController)
            .GetMethod(nameof(ClientController.SetAdvanceTaxReceiptMode))!
            .GetCustomAttribute<AuthorizeAttribute>();

        authorize.ShouldNotBeNull();
        authorize!.Roles.ShouldBe("Admin,SysAdmin");
    }

    [Fact]
    public void GetAdvanceTaxReceiptMode_IsReadableByAnyAuthenticatedUser()
    {
        // No method-level [Authorize(Roles = ...)]; the class-level [Authorize]
        // still requires authentication.
        typeof(ClientController)
            .GetMethod(nameof(ClientController.GetAdvanceTaxReceiptMode))!
            .GetCustomAttribute<AuthorizeAttribute>()
            .ShouldBeNull();

        typeof(ClientController)
            .GetCustomAttribute<AuthorizeAttribute>()
            .ShouldNotBeNull();
    }
}

/// <summary>
/// Azure Functions wrapper tests — the wrappers must reproduce the controller's
/// authorization attributes in code, then delegate to the real controller.
/// </summary>
public class AdvanceTaxReceiptModeFunctionsTests
{
    private readonly IClientService _clientService = Substitute.For<IClientService>();

    private ClientFunctions BuildSut() =>
        new(new ClientController(_clientService, Substitute.For<ILogger<ClientController>>()));

    /// <summary>Builds a request with the given roles; no roles at all = anonymous.</summary>
    private static HttpRequest BuildRequest(string? jsonBody = null, params string[] roles)
    {
        var ctx = new DefaultHttpContext();

        if (roles.Length > 0)
        {
            var claims = new List<Claim> { new(ClaimTypes.NameIdentifier, "1") };
            claims.AddRange(roles.Select(r => new Claim(ClaimTypes.Role, r)));
            // A non-null authenticationType is what makes IsAuthenticated true.
            ctx.User = new ClaimsPrincipal(new ClaimsIdentity(claims, authenticationType: "TestAuth"));
        }

        ctx.Request.Body = jsonBody == null
            ? new MemoryStream()
            : new MemoryStream(Encoding.UTF8.GetBytes(jsonBody));
        ctx.Request.ContentType = "application/json";

        return ctx.Request;
    }

    private static string Body(EAdvanceTaxReceiptMode mode) =>
        JsonSerializer.Serialize(new { mode });

    // ── GET wrapper ─────────────────────────────────────────────────────────

    [Fact]
    public async Task Client_GetAdvanceTaxReceiptMode_Anonymous_Returns401()
    {
        var result = await BuildSut().Client_GetAdvanceTaxReceiptMode(BuildRequest());

        result.ShouldBeOfType<UnauthorizedResult>();
    }

    [Fact]
    public async Task Client_GetAdvanceTaxReceiptMode_PlainUser_ReturnsOkWithMode()
    {
        _clientService.GetAdvanceTaxReceiptModeAsync(Arg.Any<CancellationToken>())
            .Returns(EAdvanceTaxReceiptMode.Disabled);

        var result = await BuildSut().Client_GetAdvanceTaxReceiptMode(BuildRequest(roles: "User"));

        var ok = result.ShouldBeOfType<OkObjectResult>();
        ok.Value.ShouldBe(EAdvanceTaxReceiptMode.Disabled);
    }

    // ── PUT wrapper ─────────────────────────────────────────────────────────

    [Fact]
    public async Task Client_SetAdvanceTaxReceiptMode_Anonymous_Returns401()
    {
        var result = await BuildSut().Client_SetAdvanceTaxReceiptMode(
            BuildRequest(Body(EAdvanceTaxReceiptMode.Disabled)));

        result.ShouldBeOfType<UnauthorizedResult>();
        await _clientService.DidNotReceive().SetAdvanceTaxReceiptModeAsync(
            Arg.Any<EAdvanceTaxReceiptMode>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Client_SetAdvanceTaxReceiptMode_PlainUser_IsForbidden()
    {
        // Functions never evaluates [Authorize(Roles = ...)] — the wrapper's own
        // role check is the only thing standing between a User and this setting.
        var result = await BuildSut().Client_SetAdvanceTaxReceiptMode(
            BuildRequest(Body(EAdvanceTaxReceiptMode.Disabled), "User"));

        result.ShouldBeOfType<ForbidResult>();
        await _clientService.DidNotReceive().SetAdvanceTaxReceiptModeAsync(
            Arg.Any<EAdvanceTaxReceiptMode>(), Arg.Any<CancellationToken>());
    }

    [Theory]
    [InlineData("Admin")]
    [InlineData("SysAdmin")]
    public async Task Client_SetAdvanceTaxReceiptMode_PrivilegedRole_DelegatesToService(string role)
    {
        _clientService.SetAdvanceTaxReceiptModeAsync(
                Arg.Any<EAdvanceTaxReceiptMode>(), Arg.Any<CancellationToken>())
            .Returns(true);

        var result = await BuildSut().Client_SetAdvanceTaxReceiptMode(
            BuildRequest(Body(EAdvanceTaxReceiptMode.OnAnyPayment), role));

        var ok = result.ShouldBeOfType<OkObjectResult>();
        ok.Value.ShouldBe(EAdvanceTaxReceiptMode.OnAnyPayment);
        await _clientService.Received(1).SetAdvanceTaxReceiptModeAsync(
            EAdvanceTaxReceiptMode.OnAnyPayment, Arg.Any<CancellationToken>());
    }
}

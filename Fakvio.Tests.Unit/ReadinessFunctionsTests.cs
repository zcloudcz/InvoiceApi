using System.Security.Claims;
using Fakvio.API.Controller;
using Fakvio.Application.Service;
using Fakvio.Contracts.Dto.Readiness;
using Fakvio.Domain.Enums;
using Fakvio.Functions.Generated;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;
using NSubstitute;
using Shouldly;

namespace Fakvio.Tests.Unit;

/// <summary>
/// Tests for the Azure Functions half of <c>GET /api/readiness</c> (issue #209).
///
/// The API host is covered by <c>ReadinessEndpointTests</c> in Fakvio.Tests.Integration.
/// The Functions host runs the same controller but reimplements two things the MVC
/// pipeline normally does — the <c>[Authorize]</c> gate and query-string binding — so
/// those two are what these tests pin. Everything else is the controller, tested once.
///
/// The controller is real (not a mock): the point is to prove the wrapper actually
/// delegates, following the IssueFinalInvoiceFunctionsTests pattern.
/// </summary>
public class ReadinessFunctionsTests
{
    private readonly ITenantReadinessService _readinessService = Substitute.For<ITenantReadinessService>();

    private ReadinessFunctions BuildSut() =>
        new(new ReadinessController(_readinessService, Substitute.For<ILogger<ReadinessController>>()));

    [Fact]
    public async Task Readiness_GetReport_Anonymous_Returns401()
    {
        var result = await BuildSut().Readiness_GetReport(BuildRequest(authenticated: false));

        result.ShouldBeOfType<UnauthorizedResult>(
            "The Functions host has no [Authorize] filter — the wrapper must do the check itself.");
        await _readinessService.DidNotReceiveWithAnyArgs().GetReportAsync();
    }

    /// <summary>
    /// The wrapper parses <c>?issuerId=</c> by hand. If that parse were dropped the endpoint
    /// would silently report on every issuer of the tenant — a wrong answer, not an error.
    /// </summary>
    [Fact]
    public async Task Readiness_GetReport_PassesIssuerIdFromQueryToTheService()
    {
        _readinessService.GetReportAsync(Arg.Any<long?>(), Arg.Any<EDocumentType?>(), Arg.Any<CancellationToken>())
            .Returns(new ReadinessReportDto());

        var result = await BuildSut().Readiness_GetReport(
            BuildRequest(authenticated: true, issuerId: "4242"));

        result.ShouldBeOfType<OkObjectResult>();
        await _readinessService.Received(1).GetReportAsync(4242L, Arg.Any<EDocumentType?>(), Arg.Any<CancellationToken>());
    }

    /// <summary>
    /// No query parameter must reach the service as null (= check all issuers), not as 0.
    /// </summary>
    [Fact]
    public async Task Readiness_GetReport_WithoutIssuerId_PassesNull()
    {
        _readinessService.GetReportAsync(Arg.Any<long?>(), Arg.Any<EDocumentType?>(), Arg.Any<CancellationToken>())
            .Returns(new ReadinessReportDto());

        await BuildSut().Readiness_GetReport(BuildRequest(authenticated: true));

        await _readinessService.Received(1).GetReportAsync(null, Arg.Any<EDocumentType?>(), Arg.Any<CancellationToken>());
    }

    /// <summary>
    /// The 404 for an unknown issuer is produced by the controller, so it must survive the
    /// wrapper's response normalization instead of being flattened into a 200.
    /// </summary>
    [Fact]
    public async Task Readiness_GetReport_UnknownIssuerId_Returns404()
    {
        _readinessService.GetReportAsync(Arg.Any<long?>(), Arg.Any<EDocumentType?>(), Arg.Any<CancellationToken>())
            .Returns(new ReadinessReportDto
            {
                Issues =
                [
                    new ReadinessIssueDto
                    {
                        Code = ReadinessCodes.IssuerMissing,
                        Severity = EReadinessSeverity.Blocking
                    }
                ]
            });

        var result = await BuildSut().Readiness_GetReport(
            BuildRequest(authenticated: true, issuerId: "999999"));

        result.ShouldBeOfType<NotFoundObjectResult>();
    }

    /// <summary>
    /// Minimal HttpRequest for the wrapper: an authentication type on the identity is what
    /// makes IsAuthenticated true, and the query collection is what the parse reads.
    /// </summary>
    private static HttpRequest BuildRequest(bool authenticated, string? issuerId = null)
    {
        var ctx = new DefaultHttpContext();

        if (authenticated)
            ctx.User = new ClaimsPrincipal(new ClaimsIdentity(
                [new Claim(ClaimTypes.NameIdentifier, "1")], authenticationType: "TestAuth"));

        if (issuerId != null)
            ctx.Request.QueryString = new QueryString($"?issuerId={issuerId}");

        return ctx.Request;
    }
}

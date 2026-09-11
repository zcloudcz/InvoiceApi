// ============================================================================
// CompanyControllerErrorHandlingTests — coverage for issue #174.
//
// CompanyController.GetCompanyById used to send ex.ToString() — the FULL .NET
// stack trace — to the client on any unhandled failure. Same information-
// disclosure class as issue #156 (ChatController), fixed the same way: the
// full exception goes to ILogger → DatabaseLogger → AppLog, the client only
// gets a safe message plus a CorrelationId to quote when reporting the issue.
// See Fakvio.Tests.Unit/ChatControllerErrorHandlingTests.cs for the original.
// ============================================================================

using AresService;
using Fakvio.API.Controller;
using Fakvio.Application.Service;
using Fakvio.Infrastructure.Data;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using NSubstitute;
using Shouldly;

namespace Fakvio.Tests.Unit;

public class CompanyControllerErrorHandlingTests : IDisposable
{
    private const string TestCorrelationId = "11111111-2222-3333-4444-555555555555";

    private readonly MasterDbContext _context;
    private readonly ILogger<CompanyController> _logger = Substitute.For<ILogger<CompanyController>>();

    public CompanyControllerErrorHandlingTests()
    {
        var options = new DbContextOptionsBuilder<MasterDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;
        _context = new MasterDbContext(options);
    }

    /// <summary>
    /// Builds the controller with a CorrelationId already present in HttpContext.Items —
    /// exactly what CorrelationIdMiddleware does for every real request before the controller
    /// runs. Dependencies unrelated to GetCompanyById (ARES, provisioning, email, credential
    /// protector) are not exercised by that endpoint, so plain substitutes are enough.
    /// </summary>
    private CompanyController BuildController()
    {
        var controller = new CompanyController(
            Substitute.For<IAresService>(),
            Substitute.For<ITenantProvisioningService>(),
            Substitute.For<IEmailService>(),
            Substitute.For<ICredentialProtector>(),
            _context,
            _logger);

        var httpContext = new DefaultHttpContext();
        httpContext.Items["CorrelationId"] = TestCorrelationId;
        controller.ControllerContext = new ControllerContext { HttpContext = httpContext };
        return controller;
    }

    /// <summary>
    /// Regression test for issue #174: an unexpected failure must produce HTTP 500 with a short,
    /// safe message quoting the CorrelationId — never ex.ToString() or any part of it.
    ///
    /// Disposing the context before the call is how this test forces
    /// _masterContext.Client.FirstOrDefaultAsync(...) to throw ObjectDisposedException without
    /// needing a real database error — the whole point is that the catch block does not care
    /// which exception type it is, so this stands in for any infrastructure failure.
    /// </summary>
    [Fact]
    public async Task GetCompanyById_WhenQueryThrows_ReturnsSafeMessageWithCorrelationId()
    {
        // Arrange
        var controller = BuildController();
        _context.Dispose();

        // Act
        var result = await controller.GetCompanyById(id: 7);

        // Assert — HTTP 500, sanitized body.
        var objectResult = result.Result.ShouldBeOfType<ObjectResult>();
        objectResult.StatusCode.ShouldBe(StatusCodes.Status500InternalServerError);

        var body = objectResult.Value!.ToString()!;
        body.ShouldNotContain(nameof(ObjectDisposedException));
        body.ShouldNotContain("   at ");
        body.ShouldNotContain(nameof(MasterDbContext));
        body.ShouldContain(TestCorrelationId);
    }

    /// <summary>
    /// The sanitized response must not swallow the failure — the full exception (with its type
    /// and stack trace) has to reach ILogger, which DatabaseLogger persists into AppLog under the
    /// same CorrelationId the client was given.
    /// </summary>
    [Fact]
    public async Task GetCompanyById_WhenQueryThrows_LogsFullExceptionServerSide()
    {
        // Arrange
        var controller = BuildController();
        _context.Dispose();

        // Act
        await controller.GetCompanyById(id: 7);

        // Assert — ILogger.Log received an exception object, not just a formatted string.
        _logger.Received(1).Log(
            LogLevel.Error,
            Arg.Any<EventId>(),
            Arg.Any<object>(),
            Arg.Is<Exception>(ex => ex is ObjectDisposedException),
            Arg.Any<Func<object, Exception?, string>>());
    }

    public void Dispose() => _context.Dispose();
}

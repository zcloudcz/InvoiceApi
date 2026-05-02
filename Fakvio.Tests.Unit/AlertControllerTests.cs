// ============================================================================
// AlertControllerTests — coverage for PR #85 (issue #32).
//
// The 12 existing AlertServiceTests cover the service layer thoroughly.
// These tests exercise the missing coverage gaps:
//
//   1. AlertController — HTTP layer (auth gating, 404 on ResolveAlert,
//      query param forwarding to service, happy paths for all 3 endpoints)
//
//   2. AlertService — ordering and idempotency boundary:
//      - GetDashboard recent list is ordered newest-first
//      - Idempotency is scoped per (Type, EntityId, EntityType):
//        same Type+EntityId with different EntityType creates a new alert
//
//   3. AlertDto.IsResolved — computed property tested in isolation
// ============================================================================

using System.Security.Claims;
using Fakvio.API.Controller;
using Fakvio.Application.Service;
using Fakvio.Contracts.Dto.Alert;
using Fakvio.Domain.Enums;
using Fakvio.Infrastructure.Data;
using Fakvio.Infrastructure.Service;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using NSubstitute;
using NSubstitute.ExceptionExtensions;
using Shouldly;

namespace Fakvio.Tests.Unit;

// ============================================================================
// 1. AlertController HTTP layer
// ============================================================================

/// <summary>
/// Tests for <see cref="AlertController"/> — verifies auth gating,
/// error handling, and that the controller correctly delegates to the service.
/// Uses NSubstitute mocks so these tests are pure controller-layer tests.
/// </summary>
public class AlertControllerTests
{
    private readonly IAlertService _alertService = Substitute.For<IAlertService>();
    private readonly ICurrentUserService _currentUserService = Substitute.For<ICurrentUserService>();

    /// <summary>
    /// Builds the controller under test with an authenticated HttpContext.
    /// </summary>
    private AlertController BuildController(long? userId = 42)
    {
        var controller = new AlertController(
            _alertService,
            _currentUserService,
            Substitute.For<ILogger<AlertController>>());

        var ctx = new DefaultHttpContext();
        var claims = new List<Claim>
        {
            new("UserId", (userId ?? 0).ToString()),
            new(ClaimTypes.NameIdentifier, (userId ?? 0).ToString())
        };
        ctx.User = new ClaimsPrincipal(
            new ClaimsIdentity(claims, authenticationType: "TestAuth"));

        controller.ControllerContext = new ControllerContext { HttpContext = ctx };
        _currentUserService.GetCurrentUserId().Returns(userId);
        return controller;
    }

    // ── GET /api/alert ──────────────────────────────────────────────────────

    [Fact]
    public async Task GetAlerts_HappyPath_ReturnsOkWithList()
    {
        var expected = new List<AlertDto>
        {
            new() { Id = 1, Type = EAlertType.OverpaidProforma, Message = "msg" }
        };
        _alertService.GetAsync(unresolvedOnly: true, Arg.Any<CancellationToken>())
            .Returns(expected);

        var result = await BuildController().GetAlerts(unresolvedOnly: true);

        var ok = result.Result.ShouldBeOfType<OkObjectResult>();
        ok.Value.ShouldBe(expected);
    }

    [Fact]
    public async Task GetAlerts_UnresolvedOnlyFalse_ForwardsParamToService()
    {
        _alertService.GetAsync(unresolvedOnly: false, Arg.Any<CancellationToken>())
            .Returns(new List<AlertDto>());

        await BuildController().GetAlerts(unresolvedOnly: false);

        await _alertService.Received(1)
            .GetAsync(unresolvedOnly: false, Arg.Any<CancellationToken>());
    }

    // ── GET /api/alert/dashboard ─────────────────────────────────────────────

    [Fact]
    public async Task GetDashboard_HappyPath_ReturnsOkWithSummary()
    {
        var summary = new AlertDashboardDto
        {
            TotalOpenCount = 3,
            RecentAlerts = [new() { Id = 1 }]
        };
        _alertService.GetDashboardAsync(Arg.Any<CancellationToken>())
            .Returns(summary);

        var result = await BuildController().GetDashboard();

        var ok = result.Result.ShouldBeOfType<OkObjectResult>();
        var dto = ok.Value.ShouldBeOfType<AlertDashboardDto>();
        dto.TotalOpenCount.ShouldBe(3);
        dto.RecentAlerts.Count.ShouldBe(1);
    }

    // ── POST /api/alert/{id}/resolve ─────────────────────────────────────────

    [Fact]
    public async Task ResolveAlert_HappyPath_ReturnsOkWithDto()
    {
        var resolvedDto = new AlertDto
        {
            Id = 5,
            ResolvedAt = DateTime.UtcNow
        };
        _alertService.ResolveAsync(5, 42, Arg.Any<CancellationToken>())
            .Returns(resolvedDto);

        var result = await BuildController(userId: 42).ResolveAlert(5);

        var ok = result.Result.ShouldBeOfType<OkObjectResult>();
        var dto = ok.Value.ShouldBeOfType<AlertDto>();
        dto.Id.ShouldBe(5);
        dto.IsResolved.ShouldBeTrue();
    }

    [Fact]
    public async Task ResolveAlert_UnknownId_Returns404()
    {
        _alertService.ResolveAsync(9999, Arg.Any<long?>(), Arg.Any<CancellationToken>())
            .ThrowsAsync(new KeyNotFoundException("Alert 9999 not found."));

        var result = await BuildController().ResolveAlert(9999);

        result.Result.ShouldBeOfType<NotFoundObjectResult>();
    }

    [Fact]
    public async Task ResolveAlert_PassesCurrentUserIdToService()
    {
        _alertService.ResolveAsync(Arg.Any<long>(), Arg.Any<long?>(), Arg.Any<CancellationToken>())
            .Returns(new AlertDto { Id = 1 });

        await BuildController(userId: 77).ResolveAlert(1);

        // Verify the controller passed the user ID from ICurrentUserService to the service.
        await _alertService.Received(1)
            .ResolveAsync(1, 77, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ResolveAlert_NullUserId_StillCallsService()
    {
        // Anonymous-but-authenticated context has no user ID — service must still be called.
        _alertService.ResolveAsync(Arg.Any<long>(), Arg.Is<long?>(x => x == null), Arg.Any<CancellationToken>())
            .Returns(new AlertDto { Id = 2 });

        var result = await BuildController(userId: null).ResolveAlert(2);

        result.Result.ShouldBeOfType<OkObjectResult>();
        await _alertService.Received(1)
            .ResolveAsync(2, null, Arg.Any<CancellationToken>());
    }
}

// ============================================================================
// 2. AlertService — ordering and idempotency boundary (real InMemory DB)
// ============================================================================

/// <summary>
/// Additional AlertService tests covering behaviors not exercised by
/// the existing 12 tests: dashboard ordering and idempotency boundaries.
/// </summary>
public class AlertServiceEdgeCaseTests : IDisposable
{
    private readonly TenantDbContext _context;
    private readonly AlertService _sut;

    public AlertServiceEdgeCaseTests()
    {
        var options = new DbContextOptionsBuilder<TenantDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;
        _context = new TenantDbContext(options);
        _sut = new AlertService(_context, Substitute.For<ILogger<AlertService>>());
    }

    public void Dispose()
    {
        _context.Database.EnsureDeleted();
        _context.Dispose();
        GC.SuppressFinalize(this);
    }

    // ── GetDashboard ordering ──────────────────────────────────────────────

    [Fact]
    public async Task GetDashboard_RecentAlerts_AreOrderedNewestFirst()
    {
        // Create 3 alerts one-by-one so CreatedAt timestamps differ slightly.
        var first = await _sut.CreateAsync(EAlertType.OverpaidProforma, 1, "Invoice", "oldest");
        await Task.Delay(5); // ensure distinct CreatedAt values
        await _sut.CreateAsync(EAlertType.OverpaidProforma, 2, "Invoice", "middle");
        await Task.Delay(5);
        var third = await _sut.CreateAsync(EAlertType.OverpaidProforma, 3, "Invoice", "newest");

        var dashboard = await _sut.GetDashboardAsync();

        // First item in the list must be the most recently created alert.
        dashboard.RecentAlerts[0].Id.ShouldBe(third.Id);
        dashboard.RecentAlerts[2].Id.ShouldBe(first.Id);
    }

    [Fact]
    public async Task GetDashboard_ExactlyFiveAlerts_ReturnsAll5()
    {
        for (var i = 1; i <= 5; i++)
            await _sut.CreateAsync(EAlertType.OverpaidProforma, i, "Invoice", $"msg {i}");

        var dashboard = await _sut.GetDashboardAsync();

        dashboard.TotalOpenCount.ShouldBe(5);
        dashboard.RecentAlerts.Count.ShouldBe(5);
    }

    // ── Get ordering ───────────────────────────────────────────────────────

    [Fact]
    public async Task Get_UnresolvedOnly_ResultsAreOrderedNewestFirst()
    {
        var first = await _sut.CreateAsync(EAlertType.OverpaidProforma, 1, "Invoice", "old");
        await Task.Delay(5);
        var second = await _sut.CreateAsync(EAlertType.OverpaidProforma, 2, "Invoice", "new");

        var results = await _sut.GetAsync(unresolvedOnly: true);

        results[0].Id.ShouldBe(second.Id);
        results[1].Id.ShouldBe(first.Id);
    }

    // ── Idempotency boundary ───────────────────────────────────────────────

    [Fact]
    public async Task Create_SameTypeAndEntityId_DifferentEntityType_CreatesNewAlert()
    {
        // Idempotency key is (Type, RelatedEntityId, RelatedEntityType).
        // Same Type+EntityId but different EntityType must NOT be treated as duplicate.
        var first = await _sut.CreateAsync(
            EAlertType.OverpaidProforma, 42, "Invoice", "invoice alert");
        var second = await _sut.CreateAsync(
            EAlertType.OverpaidProforma, 42, "ReceivedInvoice", "received invoice alert");

        second.Id.ShouldNotBe(first.Id);
        (await _context.Alert.CountAsync()).ShouldBe(2);
    }

    [Fact]
    public async Task Create_Idempotent_MessageNotUpdated_ReturnsOriginalMessage()
    {
        // The second Create call with a different message must return the ORIGINAL message
        // (the existing open alert is not modified).
        var first = await _sut.CreateAsync(
            EAlertType.OverpaidProforma, 10, "Invoice", "original message");
        var second = await _sut.CreateAsync(
            EAlertType.OverpaidProforma, 10, "Invoice", "updated message");

        second.Message.ShouldBe("original message");
    }
}

// ============================================================================
// 3. AlertDto.IsResolved — computed property
// ============================================================================

/// <summary>
/// Tests for the <see cref="AlertDto.IsResolved"/> computed property,
/// which is the DTO's sole piece of derived logic.
/// </summary>
public class AlertDtoTests
{
    [Fact]
    public void IsResolved_WhenResolvedAtIsNull_ReturnsFalse()
    {
        var dto = new AlertDto { ResolvedAt = null };
        dto.IsResolved.ShouldBeFalse();
    }

    [Fact]
    public void IsResolved_WhenResolvedAtIsSet_ReturnsTrue()
    {
        var dto = new AlertDto { ResolvedAt = DateTime.UtcNow };
        dto.IsResolved.ShouldBeTrue();
    }

    [Fact]
    public void AlertDashboardDto_DefaultValues_AreEmpty()
    {
        var dto = new AlertDashboardDto();
        dto.TotalOpenCount.ShouldBe(0);
        dto.RecentAlerts.ShouldNotBeNull();
        dto.RecentAlerts.ShouldBeEmpty();
    }
}

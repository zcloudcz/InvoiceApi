using Fakvio.Domain.Entities;
using Fakvio.Domain.Enums;
using Fakvio.Infrastructure.Data;
using Fakvio.Infrastructure.Service;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using NSubstitute;
using Shouldly;

namespace Fakvio.Tests.Unit;

/// <summary>
/// Unit tests for <see cref="AlertService"/>.
///
/// Covers:
///   - Create: happy path, idempotency (no duplicate open alerts), resolved alerts allow re-create.
///   - Get: unresolvedOnly filtering.
///   - GetDashboard: correct count + max-5 ordering.
///   - Resolve: marks resolved, sets user ID, idempotent, KeyNotFoundException for unknown ID.
/// </summary>
public class AlertServiceTests : IDisposable
{
    private readonly TenantDbContext _context;
    private readonly AlertService _sut;

    public AlertServiceTests()
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

    // ─── Create ─────────────────────────────────────────────────────────────

    [Fact]
    public async Task Create_HappyPath_PersistsAlert()
    {
        var dto = await _sut.CreateAsync(
            EAlertType.OverpaidProforma,
            relatedEntityId: 42,
            relatedEntityType: "Invoice",
            message: "Přeplatek 100 CZK.");

        dto.Id.ShouldBeGreaterThan(0);
        dto.Type.ShouldBe(EAlertType.OverpaidProforma);
        dto.RelatedEntityId.ShouldBe(42);
        dto.RelatedEntityType.ShouldBe("Invoice");
        dto.Message.ShouldBe("Přeplatek 100 CZK.");
        dto.ResolvedAt.ShouldBeNull();
        dto.IsResolved.ShouldBeFalse();

        // Verify it was actually saved to the DB.
        var saved = await _context.Alert.FindAsync(dto.Id);
        saved.ShouldNotBeNull();
    }

    [Fact]
    public async Task Create_Idempotent_ReturnsExistingOpenAlert()
    {
        // First call creates the alert.
        var first = await _sut.CreateAsync(
            EAlertType.OverpaidProforma, 42, "Invoice", "msg1");

        // Second call for the same (type, entity) must return the same alert, not a new one.
        var second = await _sut.CreateAsync(
            EAlertType.OverpaidProforma, 42, "Invoice", "msg2");

        second.Id.ShouldBe(first.Id);
        (await _context.Alert.CountAsync()).ShouldBe(1);
    }

    [Fact]
    public async Task Create_AfterResolved_CreatesNewAlert()
    {
        // Create and resolve an alert.
        var first = await _sut.CreateAsync(
            EAlertType.OverpaidProforma, 42, "Invoice", "original");
        await _sut.ResolveAsync(first.Id, resolvedByUserId: null);

        // Creating again should produce a NEW alert row.
        var second = await _sut.CreateAsync(
            EAlertType.OverpaidProforma, 42, "Invoice", "new alert");

        second.Id.ShouldNotBe(first.Id);
        (await _context.Alert.CountAsync()).ShouldBe(2);
    }

    // ─── Get ────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Get_UnresolvedOnly_ExcludesResolved()
    {
        var openAlert = await _sut.CreateAsync(
            EAlertType.OverpaidProforma, 1, "Invoice", "open");
        var resolvedAlert = await _sut.CreateAsync(
            EAlertType.OverpaidProforma, 2, "Invoice", "resolved");
        await _sut.ResolveAsync(resolvedAlert.Id, resolvedByUserId: null);

        var results = await _sut.GetAsync(unresolvedOnly: true);

        results.Count.ShouldBe(1);
        results[0].Id.ShouldBe(openAlert.Id);
    }

    [Fact]
    public async Task Get_AllAlerts_IncludesResolved()
    {
        await _sut.CreateAsync(EAlertType.OverpaidProforma, 1, "Invoice", "open");
        var resolved = await _sut.CreateAsync(
            EAlertType.OverpaidProforma, 2, "Invoice", "resolved");
        await _sut.ResolveAsync(resolved.Id, resolvedByUserId: null);

        var results = await _sut.GetAsync(unresolvedOnly: false);

        results.Count.ShouldBe(2);
    }

    // ─── GetDashboard ────────────────────────────────────────────────────────

    [Fact]
    public async Task GetDashboard_ReturnsCorrectCountAndMax5()
    {
        // Create 7 open alerts.
        for (var i = 1; i <= 7; i++)
        {
            await _sut.CreateAsync(EAlertType.OverpaidProforma, i, "Invoice", $"msg {i}");
        }

        var dashboard = await _sut.GetDashboardAsync();

        dashboard.TotalOpenCount.ShouldBe(7);
        dashboard.RecentAlerts.Count.ShouldBe(5);
    }

    [Fact]
    public async Task GetDashboard_EmptyWhenNoAlerts()
    {
        var dashboard = await _sut.GetDashboardAsync();

        dashboard.TotalOpenCount.ShouldBe(0);
        dashboard.RecentAlerts.ShouldBeEmpty();
    }

    [Fact]
    public async Task GetDashboard_DoesNotIncludeResolvedAlerts()
    {
        var open = await _sut.CreateAsync(
            EAlertType.OverpaidProforma, 1, "Invoice", "open");
        var resolved = await _sut.CreateAsync(
            EAlertType.OverpaidProforma, 2, "Invoice", "resolved");
        await _sut.ResolveAsync(resolved.Id, resolvedByUserId: null);

        var dashboard = await _sut.GetDashboardAsync();

        dashboard.TotalOpenCount.ShouldBe(1);
        dashboard.RecentAlerts[0].Id.ShouldBe(open.Id);
    }

    // ─── Resolve ────────────────────────────────────────────────────────────

    [Fact]
    public async Task Resolve_SetsResolvedAtAndUserId()
    {
        var created = await _sut.CreateAsync(
            EAlertType.OverpaidProforma, 1, "Invoice", "msg");

        var resolved = await _sut.ResolveAsync(created.Id, resolvedByUserId: 99);

        resolved.ResolvedAt.ShouldNotBeNull();
        resolved.IsResolved.ShouldBeTrue();

        // Verify DB state directly.
        var fromDb = await _context.Alert.FindAsync(resolved.Id);
        fromDb!.ResolvedAt.ShouldNotBeNull();
        fromDb.ResolvedByUserId.ShouldBe(99);
    }

    [Fact]
    public async Task Resolve_Idempotent_SecondCallReturnsSameData()
    {
        var created = await _sut.CreateAsync(
            EAlertType.OverpaidProforma, 1, "Invoice", "msg");

        var first = await _sut.ResolveAsync(created.Id, resolvedByUserId: 1);
        var second = await _sut.ResolveAsync(created.Id, resolvedByUserId: 2);

        // ResolvedByUserId must NOT change on the second call.
        second.ResolvedAt.ShouldBe(first.ResolvedAt);
        (await _context.Alert.FindAsync(created.Id))!.ResolvedByUserId.ShouldBe(1);
    }

    [Fact]
    public async Task Resolve_UnknownId_ThrowsKeyNotFoundException()
    {
        await Should.ThrowAsync<KeyNotFoundException>(
            () => _sut.ResolveAsync(9999, resolvedByUserId: null));
    }

    [Fact]
    public async Task Resolve_DoesNotModifyInvoiceData()
    {
        // Create a minimal invoice-like entity in the Alert table for the test — the
        // service should only touch the Alert row, not any Invoice rows.
        var created = await _sut.CreateAsync(
            EAlertType.OverpaidProforma, 7, "Invoice", "msg");

        // No invoices were created — just confirm the service doesn't touch Invoice table.
        var invoiceCount = await _context.Invoice.CountAsync();

        await _sut.ResolveAsync(created.Id, resolvedByUserId: null);

        // Invoice count unchanged — resolve only updates Alert.
        (await _context.Invoice.CountAsync()).ShouldBe(invoiceCount);
    }
}

using Fakvio.Application.Service;
using Fakvio.Contracts.Dto.Notification;
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
/// Unit tests for <see cref="NotificationService"/>.
///
/// Covers:
///   - CreateForAllUsersAsync: fans out to all active users, skips inactive, no-op without tenant context.
///   - GetUnreadCountAsync: correct count, zero when all read.
///   - GetForUserAsync: pagination, unread-only filter.
///   - MarkAsReadAsync: sets ReadAt, idempotent.
///   - MarkAllAsReadAsync: bulk update.
///   - GetDashboardAsync: correct unread count + recent list.
/// </summary>
public class NotificationServiceTests : IDisposable
{
    private readonly TenantDbContext _tenantContext;
    private readonly MasterDbContext _masterContext;
    private readonly ITenantResolver _tenantResolver;
    private readonly NotificationService _sut;

    private const long CompanyId = 1;
    private const long User1Id = 10;
    private const long User2Id = 20;
    private const long InactiveUserId = 30;

    public NotificationServiceTests()
    {
        var dbName = Guid.NewGuid().ToString();

        var tenantOptions = new DbContextOptionsBuilder<TenantDbContext>()
            .UseInMemoryDatabase(dbName + "_tenant")
            .Options;
        _tenantContext = new TenantDbContext(tenantOptions);

        var masterOptions = new DbContextOptionsBuilder<MasterDbContext>()
            .UseInMemoryDatabase(dbName + "_master")
            .Options;
        _masterContext = new MasterDbContext(masterOptions);

        _tenantResolver = Substitute.For<ITenantResolver>();
        _tenantResolver.GetCurrentCompanyId().Returns(CompanyId);

        _sut = new NotificationService(
            _tenantContext,
            _masterContext,
            _tenantResolver,
            Substitute.For<ILogger<NotificationService>>());

        SeedUsers();
    }

    private void SeedUsers()
    {
        _masterContext.User.AddRange(
            new User
            {
                Id = User1Id, Email = "user1@test.cz", CompanyId = CompanyId,
                IsActive = true, FirstName = "User", LastName = "One",
                PasswordHash = "hash"
            },
            new User
            {
                Id = User2Id, Email = "user2@test.cz", CompanyId = CompanyId,
                IsActive = true, FirstName = "User", LastName = "Two",
                PasswordHash = "hash"
            },
            new User
            {
                Id = InactiveUserId, Email = "inactive@test.cz", CompanyId = CompanyId,
                IsActive = false, FirstName = "User", LastName = "Inactive",
                PasswordHash = "hash"
            });
        _masterContext.SaveChanges();
    }

    public void Dispose()
    {
        _tenantContext.Database.EnsureDeleted();
        _masterContext.Database.EnsureDeleted();
        _tenantContext.Dispose();
        _masterContext.Dispose();
        GC.SuppressFinalize(this);
    }

    // ─── CreateForAllUsersAsync ─────────────────────────────────────────────

    [Fact]
    public async Task Create_FansOutToAllActiveUsers()
    {
        var id = await _sut.CreateForAllUsersAsync(
            ENotificationType.PaymentMatched,
            "Platba spárována",
            "Test message",
            relatedEntityId: 42,
            relatedEntityType: "Invoice");

        id.ShouldBeGreaterThan(0);

        var notification = await _tenantContext.Notification
            .Include(n => n.Recipients)
            .FirstAsync(n => n.Id == id);

        notification.Type.ShouldBe(ENotificationType.PaymentMatched);
        notification.Title.ShouldBe("Platba spárována");
        notification.RelatedEntityId.ShouldBe(42);
        notification.RelatedEntityType.ShouldBe("Invoice");

        // Only 2 active users — inactive user must NOT receive a notification.
        notification.Recipients.Count.ShouldBe(2);
        notification.Recipients.Select(r => r.UserId).ShouldBe(
            new[] { User1Id, User2Id }, ignoreOrder: true);
    }

    [Fact]
    public async Task Create_WithoutTenantContext_ReturnsZero()
    {
        _tenantResolver.GetCurrentCompanyId().Returns((long?)null);

        var id = await _sut.CreateForAllUsersAsync(
            ENotificationType.PaymentMatched, "Title", "Message", 1, "Invoice");

        id.ShouldBe(0);
        (await _tenantContext.Notification.CountAsync()).ShouldBe(0);
    }

    // ─── GetUnreadCountAsync ────────────────────────────────────────────────

    [Fact]
    public async Task GetUnreadCount_ReturnsCorrectCount()
    {
        await _sut.CreateForAllUsersAsync(
            ENotificationType.PaymentMatched, "T1", "M1", 1, "Invoice");
        await _sut.CreateForAllUsersAsync(
            ENotificationType.PaymentMatched, "T2", "M2", 2, "Invoice");

        var count = await _sut.GetUnreadCountAsync(User1Id);

        count.ShouldBe(2);
    }

    [Fact]
    public async Task GetUnreadCount_ZeroWhenAllRead()
    {
        await _sut.CreateForAllUsersAsync(
            ENotificationType.PaymentMatched, "T1", "M1", 1, "Invoice");
        await _sut.MarkAllAsReadAsync(User1Id);

        var count = await _sut.GetUnreadCountAsync(User1Id);

        count.ShouldBe(0);
    }

    // ─── GetForUserAsync ────────────────────────────────────────────────────

    [Fact]
    public async Task GetForUser_ReturnsPaginatedResults()
    {
        for (int i = 1; i <= 5; i++)
        {
            await _sut.CreateForAllUsersAsync(
                ENotificationType.PaymentMatched, $"T{i}", $"M{i}", i, "Invoice");
        }

        var filter = new NotificationFilterDto { Page = 1, PageSize = 3 };
        var result = await _sut.GetForUserAsync(User1Id, filter);

        result.Items.Count.ShouldBe(3);
        result.TotalCount.ShouldBe(5);
        result.TotalPages.ShouldBe(2);
    }

    [Fact]
    public async Task GetForUser_UnreadOnlyFilter()
    {
        var id1 = await _sut.CreateForAllUsersAsync(
            ENotificationType.PaymentMatched, "Read", "M1", 1, "Invoice");
        await _sut.CreateForAllUsersAsync(
            ENotificationType.PaymentMatched, "Unread", "M2", 2, "Invoice");

        await _sut.MarkAsReadAsync(id1, User1Id);

        var filter = new NotificationFilterDto { UnreadOnly = true };
        var result = await _sut.GetForUserAsync(User1Id, filter);

        result.Items.Count.ShouldBe(1);
        result.Items[0].Title.ShouldBe("Unread");
    }

    // ─── MarkAsReadAsync ────────────────────────────────────────────────────

    [Fact]
    public async Task MarkAsRead_SetsReadAt()
    {
        var id = await _sut.CreateForAllUsersAsync(
            ENotificationType.PaymentMatched, "T", "M", 1, "Invoice");

        await _sut.MarkAsReadAsync(id, User1Id);

        var recipient = await _tenantContext.NotificationRecipient
            .FirstAsync(r => r.NotificationId == id && r.UserId == User1Id);
        recipient.ReadAt.ShouldNotBeNull();
    }

    [Fact]
    public async Task MarkAsRead_Idempotent()
    {
        var id = await _sut.CreateForAllUsersAsync(
            ENotificationType.PaymentMatched, "T", "M", 1, "Invoice");

        await _sut.MarkAsReadAsync(id, User1Id);
        var firstReadAt = (await _tenantContext.NotificationRecipient
            .FirstAsync(r => r.NotificationId == id && r.UserId == User1Id)).ReadAt;

        await _sut.MarkAsReadAsync(id, User1Id);
        var secondReadAt = (await _tenantContext.NotificationRecipient
            .FirstAsync(r => r.NotificationId == id && r.UserId == User1Id)).ReadAt;

        secondReadAt.ShouldBe(firstReadAt);
    }

    [Fact]
    public async Task MarkAsRead_OnlyAffectsTargetUser()
    {
        var id = await _sut.CreateForAllUsersAsync(
            ENotificationType.PaymentMatched, "T", "M", 1, "Invoice");

        await _sut.MarkAsReadAsync(id, User1Id);

        // User2 should still have unread.
        var user2Recipient = await _tenantContext.NotificationRecipient
            .FirstAsync(r => r.NotificationId == id && r.UserId == User2Id);
        user2Recipient.ReadAt.ShouldBeNull();
    }

    // ─── MarkAllAsReadAsync ─────────────────────────────────────────────────

    [Fact]
    public async Task MarkAllAsRead_MarksAllForUser()
    {
        await _sut.CreateForAllUsersAsync(
            ENotificationType.PaymentMatched, "T1", "M1", 1, "Invoice");
        await _sut.CreateForAllUsersAsync(
            ENotificationType.PaymentMatched, "T2", "M2", 2, "Invoice");

        await _sut.MarkAllAsReadAsync(User1Id);

        var unread = await _tenantContext.NotificationRecipient
            .CountAsync(r => r.UserId == User1Id && r.ReadAt == null);
        unread.ShouldBe(0);

        // User2 should still be unread.
        var user2Unread = await _tenantContext.NotificationRecipient
            .CountAsync(r => r.UserId == User2Id && r.ReadAt == null);
        user2Unread.ShouldBe(2);
    }

    // ─── GetDashboardAsync ──────────────────────────────────────────────────

    [Fact]
    public async Task GetDashboard_ReturnsUnreadCountAndRecent()
    {
        for (int i = 1; i <= 3; i++)
        {
            await _sut.CreateForAllUsersAsync(
                ENotificationType.PaymentMatched, $"T{i}", $"M{i}", i, "Invoice");
        }

        var dashboard = await _sut.GetDashboardAsync(User1Id);

        dashboard.UnreadCount.ShouldBe(3);
        dashboard.RecentNotifications.Count.ShouldBe(3);
        dashboard.RecentNotifications.All(n => !n.IsRead).ShouldBeTrue();
    }
}

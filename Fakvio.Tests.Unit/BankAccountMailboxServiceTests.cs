using Fakvio.Application.Service;
using Fakvio.Domain.Entities;
using Fakvio.Infrastructure.Data;
using Fakvio.Infrastructure.Service;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using NSubstitute;
using Shouldly;

namespace Fakvio.Tests.Unit;

/// <summary>
/// Lifecycle tests for <see cref="BankAccountMailboxService"/>. We use real
/// in-memory DbContexts for both the tenant and master schemas so we can
/// assert that MasterMailboxIndex rows are kept in sync.
/// </summary>
public class BankAccountMailboxServiceTests : IDisposable
{
    private readonly TenantDbContext _tenant;
    private readonly MasterDbContext _master;
    private readonly BankAccountMailboxService _sut;
    private readonly IAliasGenerator _aliasGen;

    private long _bankAccountId;

    public BankAccountMailboxServiceTests()
    {
        var tenantOpts = new DbContextOptionsBuilder<TenantDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString()).Options;
        var masterOpts = new DbContextOptionsBuilder<MasterDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString()).Options;

        _tenant = new TenantDbContext(tenantOpts);
        _master = new MasterDbContext(masterOpts);

        _aliasGen = Substitute.For<IAliasGenerator>();
        // Default: return a counter-based alias so collisions never happen.
        var counter = 0;
        _aliasGen.Generate().Returns(_ => $"pay-test{++counter:D6}");

        var resolver = Substitute.For<ITenantResolver>();
        resolver.GetCurrentCompanyId().Returns(1L);

        var factory = Substitute.For<ITenantDbContextFactory>();
        factory.ResolveSchemaAsync(1L, Arg.Any<CancellationToken>()).Returns("tenant_1");

        _sut = new BankAccountMailboxService(
            _tenant,
            _master,
            _aliasGen,
            resolver,
            factory,
            Substitute.For<ILogger<BankAccountMailboxService>>());

        var bankAccount = new BankAccount
        {
            ClientId = 1,
            AccountNumber = "1/0100",
            CurrencyCode = "CZK",
        };
        _tenant.BankAccount.Add(bankAccount);
        _tenant.SaveChanges();
        _bankAccountId = bankAccount.Id;

        // Seed a settings row so BuildDtoAsync can read the inbound domain.
        _master.PaymentMatchingSystemSettings.Add(new PaymentMatchingSystemSettings
        {
            InboundDomain = "pay.fakvio.cz",
        });
        _master.SaveChanges();
    }

    public void Dispose()
    {
        _tenant.Database.EnsureDeleted();
        _master.Database.EnsureDeleted();
        _tenant.Dispose();
        _master.Dispose();
        GC.SuppressFinalize(this);
    }

    [Fact]
    public async Task Get_ReturnsNull_WhenNoMailbox()
    {
        var dto = await _sut.GetAsync(_bankAccountId);
        dto.ShouldBeNull();
    }

    [Fact]
    public async Task Activate_FirstTime_CreatesMailboxAndMasterIndex()
    {
        var dto = await _sut.ActivateAsync(_bankAccountId);

        dto.IsActive.ShouldBeTrue();
        dto.InboundAlias.ShouldStartWith("pay-test");
        dto.FullEmailAddress.ShouldEndWith("@pay.fakvio.cz");
        dto.ActiveFrom.ShouldBeLessThanOrEqualTo(DateTime.UtcNow);

        (await _tenant.BankAccountMailbox.CountAsync()).ShouldBe(1);
        (await _master.MasterMailboxIndex.CountAsync()).ShouldBe(1);

        var index = await _master.MasterMailboxIndex.FirstAsync();
        index.InboundAlias.ShouldBe(dto.InboundAlias);
        index.TenantSchema.ShouldBe("tenant_1");
        index.IsAliasRetired.ShouldBeFalse();
    }

    [Fact]
    public async Task Activate_SecondTime_ReusesAliasAndUpdatesActiveFrom()
    {
        var first = await _sut.ActivateAsync(_bankAccountId);
        var mailbox = await _tenant.BankAccountMailbox.FirstAsync();
        mailbox.IsActive = false;
        mailbox.DeactivatedAt = DateTime.UtcNow;
        mailbox.ActiveFrom = DateTime.UtcNow.AddDays(-30);
        await _tenant.SaveChangesAsync();

        var before = DateTime.UtcNow;
        var second = await _sut.ActivateAsync(_bankAccountId);

        second.InboundAlias.ShouldBe(first.InboundAlias);
        second.IsActive.ShouldBeTrue();
        second.ActiveFrom.ShouldBeGreaterThanOrEqualTo(before);
        second.DeactivatedAt.ShouldBeNull();
    }

    [Fact]
    public async Task Deactivate_SetsIsActiveFalseAndKeepsAlias()
    {
        await _sut.ActivateAsync(_bankAccountId);

        var dto = await _sut.DeactivateAsync(_bankAccountId);

        dto.IsActive.ShouldBeFalse();
        dto.DeactivatedAt.ShouldNotBeNull();
        dto.InboundAlias.ShouldStartWith("pay-test");

        // Master index row is NOT deleted — the mailbox may be reactivated later.
        (await _master.MasterMailboxIndex.CountAsync()).ShouldBe(1);
    }

    [Fact]
    public async Task Deactivate_WhenNoMailbox_Throws()
    {
        await Should.ThrowAsync<InvalidOperationException>(
            async () => await _sut.DeactivateAsync(_bankAccountId));
    }

    [Fact]
    public async Task Regenerate_RetiresOldIndexAndCreatesNew()
    {
        var first = await _sut.ActivateAsync(_bankAccountId);
        var oldAlias = first.InboundAlias;

        var second = await _sut.RegenerateAsync(_bankAccountId);

        second.InboundAlias.ShouldNotBe(oldAlias);

        var rows = await _master.MasterMailboxIndex.ToListAsync();
        rows.Count.ShouldBe(2);
        rows.Single(r => r.InboundAlias == oldAlias).IsAliasRetired.ShouldBeTrue();
        rows.Single(r => r.InboundAlias == second.InboundAlias).IsAliasRetired.ShouldBeFalse();
    }

    [Fact]
    public async Task Activate_UniqueAliasRetries_WhenGeneratorCollides()
    {
        // Pre-seed a row so the first generated alias already exists in master,
        // forcing the service to loop and try again.
        _master.MasterMailboxIndex.Add(new MasterMailboxIndex
        {
            InboundAlias = "pay-test000001",
            TenantSchema = "tenant_99",
            TenantBankAccountMailboxId = 999,
            IsAliasRetired = false,
        });
        await _master.SaveChangesAsync();

        var dto = await _sut.ActivateAsync(_bankAccountId);

        // The second call to Generate() produced pay-test000002 — that should be our alias.
        dto.InboundAlias.ShouldBe("pay-test000002");
    }
}

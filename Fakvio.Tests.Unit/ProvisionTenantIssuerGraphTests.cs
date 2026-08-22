using Fakvio.Domain.Entities;
using Fakvio.Domain.Enums;
using Fakvio.Infrastructure.Data;
using Fakvio.Infrastructure.Service;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Npgsql;
using NSubstitute;
using Shouldly;

namespace Fakvio.Tests.Unit;

/// <summary>
/// Tests for Step 2 of <see cref="TenantProvisioningService.ProvisionTenantAsync"/> —
/// the query that loads the master issuer together with everything Step 6 later copies
/// into the tenant schema.
///
/// Why this exists (issue #153): the bug was NOT in the copy loop, it was a missing
/// <c>Include</c> in this query. <see cref="CreateIssuerInTenantTests"/> hands the copy
/// method a fully populated <see cref="Client"/> object, so those tests stay green even
/// if every <c>Include</c> is deleted. Nothing in the suite exercised the query itself,
/// which is exactly where the data was being lost.
///
/// The tests below therefore run the REAL <c>ProvisionTenantAsync</c> against an
/// InMemory master database with a CLEARED change tracker (see
/// <see cref="LoadIssuerViaProvisioningAsync"/>) — without that clear, EF's identity
/// resolution would re-attach the navigations from the seeding call and the assertions
/// would pass no matter what the query asks for.
///
/// Provisioning is expected to blow up at Step 3 (CREATE SCHEMA): the data source points
/// at an unreachable endpoint on purpose, so these tests can never touch a real database
/// or leave a stray schema behind. Steps 3+ are covered by
/// <c>Fakvio.Tests.Integration/TenantIssuerProvisioningDatabaseTests</c>.
/// </summary>
public class ProvisionTenantIssuerGraphTests : IDisposable
{
    /// <summary>
    /// Port 1 is reserved and never listening, so every connection attempt fails fast.
    /// Building the data source itself does no I/O — only Step 3 onwards would.
    /// </summary>
    private const string UnreachableConnectionString =
        "Host=127.0.0.1;Port=1;Database=fakvio;Username=fakvio;Password=none;Timeout=1";

    private const long TestCompanyId = 42;

    private readonly MasterDbContext _masterContext;
    private readonly NpgsqlDataSource _dataSource;
    private readonly TenantProvisioningService _service;

    public ProvisionTenantIssuerGraphTests()
    {
        var options = new DbContextOptionsBuilder<MasterDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;

        _masterContext = new MasterDbContext(options);
        _dataSource = new NpgsqlDataSourceBuilder(UnreachableConnectionString).Build();

        var dataSourceFactory = Substitute.For<INpgsqlDataSourceFactory>();
        dataSourceFactory.Root.Returns(_dataSource);
        dataSourceFactory.GetForSchema(Arg.Any<string>(), Arg.Any<bool>()).Returns(_dataSource);

        _service = new TenantProvisioningService(
            _masterContext,
            dataSourceFactory,
            _dataSource,
            Substitute.For<ILogger<TenantProvisioningService>>());
    }

    public void Dispose()
    {
        _masterContext.Dispose();
        _dataSource.Dispose();
        GC.SuppressFinalize(this);
    }

    // ─── Arrange helpers ──────────────────────────────────────────────────────

    /// <summary>
    /// Seeds a master issuer that owns one of every navigation Step 6 copies,
    /// plus the CompanySystemSettings row Step 1 requires.
    /// </summary>
    private async Task SeedMasterIssuerAsync()
    {
        _masterContext.Client.Add(new Client
        {
            Id = TestCompanyId,
            CompanyName = "Test Company s.r.o.",
            RegistrationNumber = "12345678",
            IsIssuer = true,
            IsActive = true,
            Address =
            [
                new Address
                {
                    AddressType = EAddressType.Billing,
                    Street = "Hlavní 1",
                    City = "Praha",
                    PostalCode = "11000",
                    Country = "CZ",
                    IsPrimary = true
                }
            ],
            Contact =
            [
                new Contact
                {
                    ContactType = EContactType.Email,
                    ContactValue = "info@test.cz",
                    IsPrimary = true
                }
            ],
            BankAccount =
            [
                new BankAccount
                {
                    Id = 1,
                    ClientId = TestCompanyId,
                    AccountNumber = "2400123456/2010",
                    CurrencyCode = "CZK",
                    IsDefault = true
                }
            ],
            BillingSettings = new BillingSettings
            {
                Id = 7,
                ClientId = TestCompanyId,
                DueDays = 21,
                DefaultPaymentMethod = EPaymentMethod.BankTransfer
            }
        });

        _masterContext.CompanySystemSettings.Add(new CompanySystemSettings
        {
            CompanyId = TestCompanyId,
            SchemaName = $"tenant_{TestCompanyId}",
            IsProvisioned = false,
            IsActive = false
        });

        await _masterContext.SaveChangesAsync();
    }

    /// <summary>
    /// Runs provisioning far enough to execute the Step 2 query and returns the issuer
    /// instance that query materialised, read back from the master change tracker.
    ///
    /// The <c>ChangeTracker.Clear()</c> is the whole point: it drops the seeded object
    /// graph so the navigations on the returned entity can only have been filled in by
    /// the <c>Include</c>s inside <c>ProvisionTenantAsync</c>.
    ///
    /// The exception is expected and irrelevant here — it comes from Step 3, which needs
    /// a real PostgreSQL server. <see cref="ProvisionTenant_FailsNoEarlierThanSchemaCreation"/>
    /// pins that assumption so a regression in Step 2 cannot hide behind this catch.
    /// </summary>
    private async Task<Client> LoadIssuerViaProvisioningAsync()
    {
        await SeedMasterIssuerAsync();
        _masterContext.ChangeTracker.Clear();

        try
        {
            await _service.ProvisionTenantAsync(TestCompanyId);
        }
        catch (InvalidOperationException)
        {
            // Step 3 onwards cannot run without a database — see method summary.
        }

        return _masterContext.ChangeTracker
            .Entries<Client>()
            .Select(entry => entry.Entity)
            .ShouldHaveSingleItem();
    }

    // ─── Step 2 loads everything Step 6 copies (issue #153) ───────────────────

    [Fact]
    public async Task ProvisionTenant_LoadsIssuerWithBankAccounts()
    {
        var issuer = await LoadIssuerViaProvisioningAsync();

        issuer.BankAccount.ShouldHaveSingleItem()
            .AccountNumber.ShouldBe("2400123456/2010",
                "without the BankAccount include the tenant issuer is created with no payment destination");
    }

    [Fact]
    public async Task ProvisionTenant_LoadsIssuerWithBillingSettings()
    {
        var issuer = await LoadIssuerViaProvisioningAsync();

        issuer.BillingSettings.ShouldNotBeNull(
            "without the BillingSettings include the tenant issuer gets no DueDays and no default payment method")
            .DueDays.ShouldBe(21);
    }

    [Fact]
    public async Task ProvisionTenant_LoadsIssuerWithAddressesAndContacts()
    {
        var issuer = await LoadIssuerViaProvisioningAsync();

        issuer.Address.ShouldHaveSingleItem().City.ShouldBe("Praha");
        issuer.Contact.ShouldHaveSingleItem().ContactValue.ShouldBe("info@test.cz");
    }

    // ─── Guards that keep the tests above honest ──────────────────────────────

    /// <summary>
    /// Proves the Step 2 query really ran: provisioning must survive Steps 1-2 and only
    /// fail once it needs a live server. If Step 2 itself started throwing (a broken
    /// Include, an unsupported query operator), the assertions above would silently
    /// degrade into "no Client was ever loaded" instead of failing for a clear reason.
    /// </summary>
    [Fact]
    public async Task ProvisionTenant_FailsNoEarlierThanSchemaCreation()
    {
        await SeedMasterIssuerAsync();
        _masterContext.ChangeTracker.Clear();

        var act = () => _service.ProvisionTenantAsync(TestCompanyId);

        var exception = await Should.ThrowAsync<InvalidOperationException>(act);
        exception.Message.ShouldContain("Step 3",
            customMessage: "Steps 1-2 must complete against the master database before provisioning needs PostgreSQL");
    }

    /// <summary>
    /// The Step 2 filter is <c>Id == companyId AND IsIssuer</c>. A company row that is not
    /// flagged as issuer must be rejected loudly rather than provisioned from a half-empty
    /// record — this is the guard the Include list depends on.
    /// </summary>
    [Fact]
    public async Task ProvisionTenant_CompanyNotFlaggedAsIssuer_ThrowsBeforeTouchingPostgreSql()
    {
        await SeedMasterIssuerAsync();
        var issuer = await _masterContext.Client.FirstAsync(c => c.Id == TestCompanyId);
        issuer.IsIssuer = false;
        await _masterContext.SaveChangesAsync();
        _masterContext.ChangeTracker.Clear();

        var act = () => _service.ProvisionTenantAsync(TestCompanyId);

        var exception = await Should.ThrowAsync<InvalidOperationException>(act);
        exception.Message.ShouldContain("not marked as issuer");
    }
}

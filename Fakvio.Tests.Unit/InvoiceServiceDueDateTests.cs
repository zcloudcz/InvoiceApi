using Fakvio.Application.Service;
using Fakvio.Contracts.Dto.Invoice;
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
/// Unit tests for the due date resolution inside <see cref="InvoiceService.CreateInvoiceAsync"/>
/// (private helper CalculateDueDate), covering issue #106.
///
/// Resolution order verified here (highest priority first):
///   1. Explicit CreateInvoiceDto.DueDate — manual override by the user.
///   2. Client's <see cref="BillingSettings"/> — DueDateCalculationType + DueDays.
///   3. Hard-coded fallback — DaysFromIssue + 14 days (client has no BillingSettings row).
///
/// WHY THE SEED USES ITS OWN DbContext:
/// The bug in #106 was invisible to the existing test suite because the tests seeded data
/// through the very same DbContext instance that the service later used. EF Core then keeps
/// both Client and BillingSettings in its identity map and performs relationship fixup, so
/// FindAsync(clientId) returned a Client whose BillingSettings navigation was already
/// populated — even though the query never asked for it.
///
/// In production every HTTP request gets a fresh DbContext, so FindAsync issues a plain
/// "SELECT * FROM client WHERE id = @id" with no join and the navigation stays null.
/// Seeding through a separate, disposed context reproduces exactly that state: the service's
/// context starts with an empty identity map, so only what the query explicitly loads is there.
/// </summary>
public class InvoiceServiceDueDateTests : IDisposable
{
    // ─── Dependencies ────────────────────────────────────────────────────────

    private readonly DbContextOptions<TenantDbContext> _options;
    private readonly TenantDbContext _context;
    private readonly InvoiceService _service;

    // ─── Well-known seed IDs ─────────────────────────────────────────────────

    /// <summary>Customer WITH BillingSettings (DueDays = 30, DaysFromIssue).</summary>
    private const long ClientWithSettingsId = 1;

    /// <summary>Customer WITHOUT any BillingSettings row — exercises the hard-coded fallback.</summary>
    private const long ClientWithoutSettingsId = 2;

    /// <summary>Customer WITH BillingSettings using EndOfNextMonth (DueDays is ignored by that type).</summary>
    private const long ClientEndOfNextMonthId = 3;

    private const long IssuerId = 10;
    private const long CurrencyId = 1;

    /// <summary>Client-specific due days — deliberately different from the 14-day fallback.</summary>
    private const int ClientDueDays = 30;

    // Fixed issue date keeps the expected due dates deterministic across runs.
    private static readonly DateTime IssueDate = new(2026, 1, 15, 0, 0, 0, DateTimeKind.Utc);

    // ─── Constructor & Dispose ───────────────────────────────────────────────

    public InvoiceServiceDueDateTests()
    {
        _options = new DbContextOptionsBuilder<TenantDbContext>()
            .UseInMemoryDatabase(databaseName: Guid.NewGuid().ToString())
            .Options;

        SeedReferenceData();

        var logger = Substitute.For<ILogger<InvoiceService>>();
        var numberSequence = Substitute.For<INumberSequenceService>();
        numberSequence
            .GenerateNextNumberForDocumentTypeAsync(
                Arg.Any<EDocumentType>(), Arg.Any<DateTime>(),
                Arg.Any<string?>(), Arg.Any<string?>(),
                Arg.Any<CancellationToken>())
            .Returns("DUE-2026-001");

        // Fresh context AFTER seeding — empty identity map, just like a real HTTP request.
        _context = new TenantDbContext(_options);
        _service = new InvoiceService(_context, numberSequence, logger);
    }

    public void Dispose()
    {
        _context.Database.EnsureDeleted();
        _context.Dispose();
        GC.SuppressFinalize(this);
    }

    /// <summary>
    /// Seeds clients, issuer and currency through a throw-away context so that nothing
    /// stays tracked for the service's own context (see class-level remarks).
    /// </summary>
    private void SeedReferenceData()
    {
        using var seedContext = new TenantDbContext(_options);

        seedContext.Client.Add(new Client
        {
            Id = ClientWithSettingsId,
            CompanyName = "Customer With Billing Settings",
            RegistrationNumber = "CUST-001",
            IsIssuer = false,
            IsActive = true,
            BillingSettings = new BillingSettings
            {
                ClientId = ClientWithSettingsId,
                DueDays = ClientDueDays,
                DueDateCalculationType = EDueDateCalculationType.DaysFromIssue
            }
        });

        seedContext.Client.Add(new Client
        {
            Id = ClientWithoutSettingsId,
            CompanyName = "Customer Without Billing Settings",
            RegistrationNumber = "CUST-002",
            IsIssuer = false,
            IsActive = true
        });

        seedContext.Client.Add(new Client
        {
            Id = ClientEndOfNextMonthId,
            CompanyName = "Customer End Of Next Month",
            RegistrationNumber = "CUST-003",
            IsIssuer = false,
            IsActive = true,
            BillingSettings = new BillingSettings
            {
                ClientId = ClientEndOfNextMonthId,
                // DueDays must be ignored for this calculation type — a wrong value here
                // would surface if the resolution ever used DueDays unconditionally.
                DueDays = 99,
                DueDateCalculationType = EDueDateCalculationType.EndOfNextMonth
            }
        });

        // Non-VAT-payer issuer keeps invoice items simple (no VatRateId required).
        seedContext.Client.Add(new Client
        {
            Id = IssuerId,
            CompanyName = "Test Issuer Ltd.",
            RegistrationNumber = "ISS-001",
            IsIssuer = true,
            IsActive = true,
            IsVatPayer = false
        });

        seedContext.Currency.Add(new Currency
        {
            Id = CurrencyId,
            Code = "CZK",
            Name = "Czech Koruna",
            Symbol = "Kc",
            DecimalPlaces = 2,
            SortOrder = 1,
            IsActive = true
        });

        seedContext.SaveChanges();
    }

    /// <summary>
    /// Helper: minimal valid invoice DTO for the given client, with no explicit DueDate
    /// so the automatic resolution kicks in.
    /// </summary>
    private static CreateInvoiceDto BuildDto(long clientId) => new()
    {
        DocumentType = EDocumentType.Invoice,
        ClientId = clientId,
        IssuerId = IssuerId,
        CurrencyId = CurrencyId,
        IssueDate = IssueDate,
        DueDate = null,
        InvoiceItem = new List<CreateInvoiceItemDto>
        {
            new()
            {
                OrderIndex = 1,
                Description = "Consulting services",
                Quantity = 1,
                Unit = "hrs",
                UnitPrice = 1000
            }
        }
    };

    // ─── Tests ───────────────────────────────────────────────────────────────

    /// <summary>
    /// Core reproduction for #106: the client's own DueDays (30) must win over the
    /// hard-coded 14-day fallback. Before the fix the navigation property was null and
    /// the invoice silently got 2026-01-29 instead of 2026-02-14.
    /// </summary>
    [Fact]
    public async Task CreateInvoiceAsync_ClientHasBillingSettings_UsesClientDueDays()
    {
        var dto = BuildDto(ClientWithSettingsId);

        var result = await _service.CreateInvoiceAsync(dto);

        // 2026-01-15 + 30 days
        result.DueDate.ShouldBe(new DateTime(2026, 2, 14, 0, 0, 0, DateTimeKind.Utc));
    }

    /// <summary>
    /// The client's DueDateCalculationType must be honoured too, not just DueDays.
    /// EndOfNextMonth ignores DueDays entirely — January issue date leads to end of February.
    /// </summary>
    [Fact]
    public async Task CreateInvoiceAsync_ClientHasEndOfNextMonthSettings_UsesCalculationType()
    {
        var dto = BuildDto(ClientEndOfNextMonthId);

        var result = await _service.CreateInvoiceAsync(dto);

        result.DueDate.ShouldBe(new DateTime(2026, 2, 28, 0, 0, 0, DateTimeKind.Utc));
    }

    /// <summary>
    /// Bottom of the resolution order: a client with no BillingSettings row keeps the
    /// documented 14-day DaysFromIssue default. Guards against the fix accidentally
    /// changing behaviour for clients that never configured anything.
    /// </summary>
    [Fact]
    public async Task CreateInvoiceAsync_ClientHasNoBillingSettings_FallsBackToFourteenDays()
    {
        var dto = BuildDto(ClientWithoutSettingsId);

        var result = await _service.CreateInvoiceAsync(dto);

        // 2026-01-15 + 14 days
        result.DueDate.ShouldBe(new DateTime(2026, 1, 29, 0, 0, 0, DateTimeKind.Utc));
    }

    /// <summary>
    /// Top of the resolution order: an explicit DueDate from the user is a manual override
    /// and must beat the client's billing settings.
    /// </summary>
    [Fact]
    public async Task CreateInvoiceAsync_ExplicitDueDate_OverridesClientBillingSettings()
    {
        var dto = BuildDto(ClientWithSettingsId);
        dto.DueDate = new DateTime(2026, 3, 1, 0, 0, 0, DateTimeKind.Utc);

        var result = await _service.CreateInvoiceAsync(dto);

        result.DueDate.ShouldBe(new DateTime(2026, 3, 1, 0, 0, 0, DateTimeKind.Utc));
    }
}

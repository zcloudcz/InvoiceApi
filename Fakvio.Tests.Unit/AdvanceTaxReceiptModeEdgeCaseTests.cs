using AresService;
using Fakvio.Contracts.Dto.Client;
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
/// Edge-case and supplementary tests for EAdvanceTaxReceiptMode.
///
/// Complements AdvanceTaxReceiptModeTests by covering:
/// - UpdateClientAsync (via UpdateBillingSettingsDto) null-keeps-existing path
/// - UpdateClientAsync explicit mode update path
/// - BillingSettings created-on-first-set defaults (DueDays, DaysFromIssue)
/// - Enum integer values match the migration defaultValue and DB column definition
/// </summary>
public class AdvanceTaxReceiptModeEdgeCaseTests : IDisposable
{
    private readonly TenantDbContext _context;
    private readonly ClientService _service;

    public AdvanceTaxReceiptModeEdgeCaseTests()
    {
        var options = new DbContextOptionsBuilder<TenantDbContext>()
            .UseInMemoryDatabase(databaseName: Guid.NewGuid().ToString())
            .Options;

        _context = new TenantDbContext(options);
        var logger = Substitute.For<ILogger<ClientService>>();
        var aresService = Substitute.For<IAresService>();

        _service = new ClientService(_context, aresService, logger);
    }

    public void Dispose()
    {
        _context.Database.EnsureDeleted();
        _context.Dispose();
    }

    // ─── Helpers ──────────────────────────────────────────────────────────────

    /// <summary>
    /// Seeds an issuer with existing BillingSettings using the given mode.
    /// Returns the client ID.
    /// </summary>
    private async Task<long> SeedIssuerWithModeAsync(EAdvanceTaxReceiptMode mode)
    {
        var client = new Client
        {
            RegistrationNumber = "12345678",
            CompanyName = "Test s.r.o.",
            IsIssuer = true,
            IsActive = true,
            Address = new List<Address>(),
            Contact = new List<Contact>(),
            BankAccount = new List<BankAccount>(),
            CreatedAt = DateTime.UtcNow
        };
        _context.Client.Add(client);
        await _context.SaveChangesAsync();

        _context.BillingSettings.Add(new BillingSettings
        {
            ClientId = client.Id,
            AdvanceTaxReceiptMode = mode,
            DueDateCalculationType = EDueDateCalculationType.DaysFromIssue,
            DueDays = 14,
            CreatedAt = DateTime.UtcNow
        });
        await _context.SaveChangesAsync();

        return client.Id;
    }

    // ─── Enum contract ────────────────────────────────────────────────────────

    /// <summary>
    /// Enum integer values must match the migration defaultValue (1 = OnPaymentMatch)
    /// and the acceptance criteria (Disabled=0, OnPaymentMatch=1, OnAnyPayment=2).
    /// A wrong integer causes the DB column default to pick the wrong mode.
    /// </summary>
    [Fact]
    public void EAdvanceTaxReceiptMode_IntegerValues_MatchAcceptanceCriteria()
    {
        ((int)EAdvanceTaxReceiptMode.Disabled).ShouldBe(0);
        ((int)EAdvanceTaxReceiptMode.OnPaymentMatch).ShouldBe(1);
        ((int)EAdvanceTaxReceiptMode.OnAnyPayment).ShouldBe(2);
    }

    /// <summary>
    /// The DB migration uses defaultValue: 1 (OnPaymentMatch).
    /// Casting integer 1 back to the enum must give OnPaymentMatch, not an unknown value.
    /// Guards against accidental renumbering of the enum members.
    /// </summary>
    [Fact]
    public void EAdvanceTaxReceiptMode_MigrationDefault_MapsToOnPaymentMatch()
    {
        const int migrationDefault = 1;
        var mode = (EAdvanceTaxReceiptMode)migrationDefault;
        mode.ShouldBe(EAdvanceTaxReceiptMode.OnPaymentMatch);
    }

    // ─── UpdateClientAsync → UpdateBillingSettingsDto null keeps existing ─────

    /// <summary>
    /// UpdateClientAsync (PUT /api/client/{id}) uses UpdateBillingSettingsDto where
    /// AdvanceTaxReceiptMode is nullable. When the caller omits the field (null),
    /// the existing mode must NOT be changed — same partial-update contract as other fields.
    ///
    /// This is a different code path from UpdateBillingSettingsAsync and must be tested
    /// independently.
    /// </summary>
    [Theory]
    [InlineData(EAdvanceTaxReceiptMode.Disabled)]
    [InlineData(EAdvanceTaxReceiptMode.OnAnyPayment)]
    public async Task UpdateClientAsync_NullAdvanceTaxReceiptMode_KeepsExistingMode(
        EAdvanceTaxReceiptMode existingMode)
    {
        var clientId = await SeedIssuerWithModeAsync(existingMode);

        // Simulate the partial-update PUT body: BillingSettings present but mode omitted (null)
        var updateDto = new UpdateClientDto
        {
            BillingSettings = new UpdateBillingSettingsDto
            {
                AdvanceTaxReceiptMode = null  // explicit null = "don't touch"
            }
        };

        await _service.UpdateClientAsync(clientId, updateDto);

        var bs = await _context.BillingSettings
            .AsNoTracking()
            .FirstAsync(b => b.ClientId == clientId);

        bs.AdvanceTaxReceiptMode.ShouldBe(existingMode,
            $"Null AdvanceTaxReceiptMode in UpdateBillingSettingsDto must not reset the existing {existingMode} value");
    }

    /// <summary>
    /// When UpdateClientAsync receives an explicit mode value in UpdateBillingSettingsDto,
    /// it must update the stored mode. This is the "user actively changes the mode via
    /// the partial client update endpoint" path.
    /// </summary>
    [Theory]
    [InlineData(EAdvanceTaxReceiptMode.OnPaymentMatch, EAdvanceTaxReceiptMode.Disabled)]
    [InlineData(EAdvanceTaxReceiptMode.Disabled, EAdvanceTaxReceiptMode.OnAnyPayment)]
    [InlineData(EAdvanceTaxReceiptMode.OnAnyPayment, EAdvanceTaxReceiptMode.OnPaymentMatch)]
    public async Task UpdateClientAsync_ExplicitAdvanceTaxReceiptMode_UpdatesMode(
        EAdvanceTaxReceiptMode existingMode,
        EAdvanceTaxReceiptMode newMode)
    {
        var clientId = await SeedIssuerWithModeAsync(existingMode);

        var updateDto = new UpdateClientDto
        {
            BillingSettings = new UpdateBillingSettingsDto
            {
                AdvanceTaxReceiptMode = newMode
            }
        };

        await _service.UpdateClientAsync(clientId, updateDto);

        var bs = await _context.BillingSettings
            .AsNoTracking()
            .FirstAsync(b => b.ClientId == clientId);

        bs.AdvanceTaxReceiptMode.ShouldBe(newMode);
    }

    // ─── SetAdvanceTaxReceiptModeAsync — created BillingSettings defaults ─────

    /// <summary>
    /// When SetAdvanceTaxReceiptModeAsync creates BillingSettings for the first time,
    /// the created row must have sensible defaults so it is in a consistent state.
    ///
    /// Specifically: DueDays = 14 and DueDateCalculationType = DaysFromIssue.
    /// These are the production defaults that the UI pre-fills when billing settings
    /// are not yet configured.
    /// </summary>
    [Fact]
    public async Task SetAdvanceTaxReceiptMode_CreatedBillingSettings_HasSensibleDefaults()
    {
        // Seed issuer without BillingSettings
        var client = new Client
        {
            RegistrationNumber = "87654321",
            CompanyName = "No-Settings s.r.o.",
            IsIssuer = true,
            IsActive = true,
            Address = new List<Address>(),
            Contact = new List<Contact>(),
            BankAccount = new List<BankAccount>(),
            CreatedAt = DateTime.UtcNow
        };
        _context.Client.Add(client);
        await _context.SaveChangesAsync();

        await _service.SetAdvanceTaxReceiptModeAsync(EAdvanceTaxReceiptMode.OnAnyPayment);

        var bs = await _context.BillingSettings
            .AsNoTracking()
            .FirstAsync(b => b.ClientId == client.Id);

        bs.DueDays.ShouldBe(14, "DueDays should default to 14 when BillingSettings are created by SetAdvanceTaxReceiptModeAsync");
        bs.DueDateCalculationType.ShouldBe(EDueDateCalculationType.DaysFromIssue,
            "DueDateCalculationType should default to DaysFromIssue when BillingSettings are created by SetAdvanceTaxReceiptModeAsync");
        bs.ClientId.ShouldBe(client.Id, "Created BillingSettings must be linked to the issuer's client ID");
    }
}

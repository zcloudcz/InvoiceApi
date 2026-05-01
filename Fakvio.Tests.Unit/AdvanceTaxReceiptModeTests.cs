using AresService;
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
/// Unit tests for EAdvanceTaxReceiptMode persistence on the issuer's BillingSettings.
///
/// Covers:
/// - Default value when BillingSettings do not exist yet
/// - Persisting a new mode when BillingSettings already exist
/// - Creating BillingSettings on first SetAdvanceTaxReceiptModeAsync call
/// - Each valid enum value can be round-tripped
/// - Edge case: returns null when no issuer exists
/// </summary>
public class AdvanceTaxReceiptModeTests : IDisposable
{
    private readonly TenantDbContext _context;
    private readonly ClientService _service;

    public AdvanceTaxReceiptModeTests()
    {
        // Each test gets a unique in-memory database to prevent cross-test interference
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
    /// Creates a minimal issuer in the DB. Optionally attaches BillingSettings with
    /// a given mode so we can test both the "no settings yet" and "has settings" paths.
    /// </summary>
    private async Task<long> SeedIssuerAsync(EAdvanceTaxReceiptMode? existingMode = null)
    {
        var client = new Client
        {
            RegistrationNumber = "12345678",
            CompanyName = "Test Company s.r.o.",
            IsIssuer = true,
            IsActive = true,
            Address = new List<Address>(),
            Contact = new List<Contact>(),
            BankAccount = new List<BankAccount>(),
            CreatedAt = DateTime.UtcNow
        };

        _context.Client.Add(client);
        await _context.SaveChangesAsync();

        if (existingMode.HasValue)
        {
            var bs = new BillingSettings
            {
                ClientId = client.Id,
                AdvanceTaxReceiptMode = existingMode.Value,
                DueDateCalculationType = EDueDateCalculationType.DaysFromIssue,
                DueDays = 14,
                CreatedAt = DateTime.UtcNow
            };
            _context.BillingSettings.Add(bs);
            await _context.SaveChangesAsync();
        }

        return client.Id;
    }

    // ─── GetAdvanceTaxReceiptModeAsync ────────────────────────────────────────

    /// <summary>
    /// When the issuer exists but has no BillingSettings row yet,
    /// GetAdvanceTaxReceiptModeAsync should return OnPaymentMatch (the safe default).
    /// This avoids breaking existing tenants that have not configured this setting.
    /// </summary>
    [Fact]
    public async Task GetAdvanceTaxReceiptMode_IssuerWithoutBillingSettings_ReturnsOnPaymentMatch()
    {
        await SeedIssuerAsync(); // no existingMode → BillingSettings not created

        var result = await _service.GetAdvanceTaxReceiptModeAsync();

        result.ShouldNotBeNull();
        result!.Value.ShouldBe(EAdvanceTaxReceiptMode.OnPaymentMatch);
    }

    /// <summary>
    /// When the issuer has BillingSettings with Disabled mode,
    /// the service must return exactly Disabled (not the default).
    /// </summary>
    [Fact]
    public async Task GetAdvanceTaxReceiptMode_IssuerWithDisabledMode_ReturnsDisabled()
    {
        await SeedIssuerAsync(existingMode: EAdvanceTaxReceiptMode.Disabled);

        var result = await _service.GetAdvanceTaxReceiptModeAsync();

        result.ShouldNotBeNull();
        result!.Value.ShouldBe(EAdvanceTaxReceiptMode.Disabled);
    }

    /// <summary>
    /// When the issuer has BillingSettings with OnAnyPayment mode,
    /// the service must return exactly OnAnyPayment.
    /// </summary>
    [Fact]
    public async Task GetAdvanceTaxReceiptMode_IssuerWithOnAnyPaymentMode_ReturnsOnAnyPayment()
    {
        await SeedIssuerAsync(existingMode: EAdvanceTaxReceiptMode.OnAnyPayment);

        var result = await _service.GetAdvanceTaxReceiptModeAsync();

        result.ShouldNotBeNull();
        result!.Value.ShouldBe(EAdvanceTaxReceiptMode.OnAnyPayment);
    }

    /// <summary>
    /// When no issuer exists in the tenant DB (tenant not fully provisioned or corrupted),
    /// GetAdvanceTaxReceiptModeAsync must return null — not throw.
    /// The controller maps null → 404 Not Found.
    /// </summary>
    [Fact]
    public async Task GetAdvanceTaxReceiptMode_NoIssuer_ReturnsNull()
    {
        // Do NOT seed any issuer

        var result = await _service.GetAdvanceTaxReceiptModeAsync();

        result.ShouldBeNull();
    }

    // ─── SetAdvanceTaxReceiptModeAsync ────────────────────────────────────────

    /// <summary>
    /// SetAdvanceTaxReceiptModeAsync on an issuer without existing BillingSettings
    /// must CREATE a BillingSettings row and persist the requested mode.
    /// This is the "first time setup" path.
    /// </summary>
    [Fact]
    public async Task SetAdvanceTaxReceiptMode_IssuerWithoutBillingSettings_CreatesBillingSettingsWithMode()
    {
        var issuerId = await SeedIssuerAsync(); // no BillingSettings

        var success = await _service.SetAdvanceTaxReceiptModeAsync(EAdvanceTaxReceiptMode.Disabled);

        success.ShouldBeTrue();

        // Verify the row was actually created in the DB
        var bs = await _context.BillingSettings
            .FirstOrDefaultAsync(b => b.ClientId == issuerId);
        bs.ShouldNotBeNull();
        bs!.AdvanceTaxReceiptMode.ShouldBe(EAdvanceTaxReceiptMode.Disabled);
    }

    /// <summary>
    /// SetAdvanceTaxReceiptModeAsync on an issuer that already has BillingSettings
    /// must UPDATE the existing row, not create a second one.
    /// </summary>
    [Fact]
    public async Task SetAdvanceTaxReceiptMode_IssuerWithExistingBillingSettings_UpdatesExistingRow()
    {
        var issuerId = await SeedIssuerAsync(existingMode: EAdvanceTaxReceiptMode.OnPaymentMatch);

        var success = await _service.SetAdvanceTaxReceiptModeAsync(EAdvanceTaxReceiptMode.OnAnyPayment);

        success.ShouldBeTrue();

        // Only one BillingSettings row should exist
        var count = await _context.BillingSettings.CountAsync(b => b.ClientId == issuerId);
        count.ShouldBe(1);

        var bs = await _context.BillingSettings.FirstAsync(b => b.ClientId == issuerId);
        bs.AdvanceTaxReceiptMode.ShouldBe(EAdvanceTaxReceiptMode.OnAnyPayment);
    }

    /// <summary>
    /// SetAdvanceTaxReceiptModeAsync must return false when no issuer is found.
    /// This prevents a 500 error when the tenant is not provisioned correctly.
    /// </summary>
    [Fact]
    public async Task SetAdvanceTaxReceiptMode_NoIssuer_ReturnsFalse()
    {
        // Do NOT seed any issuer

        var success = await _service.SetAdvanceTaxReceiptModeAsync(EAdvanceTaxReceiptMode.Disabled);

        success.ShouldBeFalse();
    }

    /// <summary>
    /// Round-trip test: Set → Get should return the same value for all enum members.
    /// Ensures no value gets lost during serialization to the integer DB column.
    /// </summary>
    [Theory]
    [InlineData(EAdvanceTaxReceiptMode.Disabled)]
    [InlineData(EAdvanceTaxReceiptMode.OnPaymentMatch)]
    [InlineData(EAdvanceTaxReceiptMode.OnAnyPayment)]
    public async Task SetThenGet_AllModes_RoundTripsCorrectly(EAdvanceTaxReceiptMode mode)
    {
        await SeedIssuerAsync();

        await _service.SetAdvanceTaxReceiptModeAsync(mode);
        var result = await _service.GetAdvanceTaxReceiptModeAsync();

        result.ShouldNotBeNull();
        result!.Value.ShouldBe(mode);
    }
}

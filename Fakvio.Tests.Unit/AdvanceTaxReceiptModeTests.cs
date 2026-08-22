// ============================================================================
// AdvanceTaxReceiptModeTests — service-layer coverage for issue #145.
//
// EAdvanceTaxReceiptMode is a per-tenant switch stored on the ISSUER's
// BillingSettings. These tests pin down the three things that can go wrong:
//
//   1. Reading  — what happens when the issuer / the billing settings row is missing.
//   2. Writing  — the row is created on demand and every enum value round-trips.
//   3. Bleeding — the generic billing-settings update paths must NEVER overwrite
//                 the mode. That is a real bug this feature had once: the mode was
//                 mapped from a DTO whose C# default is OnPaymentMatch, so saving
//                 the billing form silently re-enabled a switch the tenant had
//                 turned off.
// ============================================================================

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
/// Tests for <see cref="ClientService.GetAdvanceTaxReceiptModeAsync"/> and
/// <see cref="ClientService.SetAdvanceTaxReceiptModeAsync"/> against an in-memory
/// tenant database (no PostgreSQL required).
/// </summary>
public class AdvanceTaxReceiptModeTests : IDisposable
{
    private readonly TenantDbContext _context;
    private readonly ClientService _service;

    public AdvanceTaxReceiptModeTests()
    {
        // A unique database per test instance — xUnit runs test classes in parallel,
        // a shared name would let tests see each other's rows.
        var options = new DbContextOptionsBuilder<TenantDbContext>()
            .UseInMemoryDatabase(databaseName: Guid.NewGuid().ToString())
            .Options;

        _context = new TenantDbContext(options);
        _service = new ClientService(
            _context,
            Substitute.For<IAresService>(),
            Substitute.For<ILogger<ClientService>>());
    }

    public void Dispose()
    {
        _context.Database.EnsureDeleted();
        _context.Dispose();
        GC.SuppressFinalize(this);
    }

    // ── Helpers ─────────────────────────────────────────────────────────────

    /// <summary>
    /// Inserts a minimal issuer. When <paramref name="existingMode"/> is given, the
    /// issuer also gets a BillingSettings row carrying that mode; otherwise the issuer
    /// has no billing settings at all (the "never opened the billing form" case).
    /// </summary>
    private async Task<long> SeedIssuerAsync(
        EAdvanceTaxReceiptMode? existingMode = null,
        string? notes = null)
    {
        var issuer = new Client
        {
            RegistrationNumber = "12345678",
            CompanyName = "Test Company s.r.o.",
            IsIssuer = true,
            IsActive = true,
            CreatedAt = DateTime.UtcNow
        };

        _context.Client.Add(issuer);
        await _context.SaveChangesAsync();

        if (existingMode.HasValue)
        {
            _context.BillingSettings.Add(new BillingSettings
            {
                ClientId = issuer.Id,
                AdvanceTaxReceiptMode = existingMode.Value,
                DueDateCalculationType = EDueDateCalculationType.DaysFromIssue,
                DueDays = 14,
                Notes = notes,
                CreatedAt = DateTime.UtcNow
            });
            await _context.SaveChangesAsync();
        }

        return issuer.Id;
    }

    /// <summary>Reads the stored mode straight from the database, bypassing the service.</summary>
    private async Task<EAdvanceTaxReceiptMode> ReadStoredModeAsync(long issuerId)
    {
        var settings = await _context.BillingSettings
            .AsNoTracking()
            .SingleAsync(b => b.ClientId == issuerId);

        return settings.AdvanceTaxReceiptMode;
    }

    // ── Get ─────────────────────────────────────────────────────────────────

    [Fact]
    public async Task GetAdvanceTaxReceiptMode_NoIssuerInTenant_ReturnsNull()
    {
        // A tenant without an issuer is not fully provisioned — the caller must be able
        // to tell that apart from "issuer exists but has no preference yet".
        var result = await _service.GetAdvanceTaxReceiptModeAsync();

        result.ShouldBeNull();
    }

    [Fact]
    public async Task GetAdvanceTaxReceiptMode_IssuerWithoutBillingSettings_ReturnsDefault()
    {
        await SeedIssuerAsync();

        var result = await _service.GetAdvanceTaxReceiptModeAsync();

        // Same default as the entity initializer — no null-handling for callers.
        result.ShouldBe(EAdvanceTaxReceiptMode.OnPaymentMatch);
    }

    [Theory]
    [InlineData(EAdvanceTaxReceiptMode.Disabled)]
    [InlineData(EAdvanceTaxReceiptMode.OnPaymentMatch)]
    [InlineData(EAdvanceTaxReceiptMode.OnAnyPayment)]
    public async Task GetAdvanceTaxReceiptMode_IssuerWithStoredMode_ReturnsStoredValue(
        EAdvanceTaxReceiptMode stored)
    {
        await SeedIssuerAsync(existingMode: stored);

        var result = await _service.GetAdvanceTaxReceiptModeAsync();

        result.ShouldBe(stored);
    }

    [Fact]
    public async Task GetAdvanceTaxReceiptMode_OnlyNonIssuerClientsExist_ReturnsNull()
    {
        // Customers have BillingSettings too, but the mode is an issuer-level setting.
        var customerId = await SeedCustomerWithModeAsync(EAdvanceTaxReceiptMode.OnAnyPayment);

        var result = await _service.GetAdvanceTaxReceiptModeAsync();

        result.ShouldBeNull();
        // Sanity check: the customer row really is there, so the null above comes from
        // the IsIssuer filter and not from an empty database.
        _context.BillingSettings.Count(b => b.ClientId == customerId).ShouldBe(1);
    }

    /// <summary>Inserts a non-issuer client that carries its own (irrelevant) mode value.</summary>
    private async Task<long> SeedCustomerWithModeAsync(EAdvanceTaxReceiptMode mode)
    {
        var customer = new Client
        {
            RegistrationNumber = "87654321",
            CompanyName = "Customer s.r.o.",
            IsIssuer = false,
            IsActive = true,
            CreatedAt = DateTime.UtcNow
        };
        _context.Client.Add(customer);
        await _context.SaveChangesAsync();

        _context.BillingSettings.Add(new BillingSettings
        {
            ClientId = customer.Id,
            AdvanceTaxReceiptMode = mode,
            CreatedAt = DateTime.UtcNow
        });
        await _context.SaveChangesAsync();

        return customer.Id;
    }

    // ── Set ─────────────────────────────────────────────────────────────────

    [Fact]
    public async Task SetAdvanceTaxReceiptMode_NoIssuerInTenant_ReturnsFalse()
    {
        var result = await _service.SetAdvanceTaxReceiptModeAsync(EAdvanceTaxReceiptMode.Disabled);

        result.ShouldBeFalse();
        _context.BillingSettings.Count().ShouldBe(0);
    }

    [Fact]
    public async Task SetAdvanceTaxReceiptMode_IssuerWithoutBillingSettings_CreatesRowWithDefaults()
    {
        var issuerId = await SeedIssuerAsync();

        var result = await _service.SetAdvanceTaxReceiptModeAsync(EAdvanceTaxReceiptMode.Disabled);

        result.ShouldBeTrue();

        var settings = await _context.BillingSettings.AsNoTracking().SingleAsync();
        settings.ClientId.ShouldBe(issuerId);
        settings.AdvanceTaxReceiptMode.ShouldBe(EAdvanceTaxReceiptMode.Disabled);
        // The remaining columns must keep the entity defaults — a half-initialised
        // billing settings row would break due-date calculation.
        settings.DueDays.ShouldBe(14);
        settings.DueDateCalculationType.ShouldBe(EDueDateCalculationType.DaysFromIssue);
    }

    [Theory]
    [InlineData(EAdvanceTaxReceiptMode.Disabled)]
    [InlineData(EAdvanceTaxReceiptMode.OnPaymentMatch)]
    [InlineData(EAdvanceTaxReceiptMode.OnAnyPayment)]
    public async Task SetAdvanceTaxReceiptMode_ExistingBillingSettings_RoundTripsEveryValue(
        EAdvanceTaxReceiptMode newMode)
    {
        var issuerId = await SeedIssuerAsync(existingMode: EAdvanceTaxReceiptMode.OnPaymentMatch);

        var result = await _service.SetAdvanceTaxReceiptModeAsync(newMode);

        result.ShouldBeTrue();
        (await ReadStoredModeAsync(issuerId)).ShouldBe(newMode);
        (await _service.GetAdvanceTaxReceiptModeAsync()).ShouldBe(newMode);
    }

    [Fact]
    public async Task SetAdvanceTaxReceiptMode_ExistingBillingSettings_LeavesOtherFieldsUntouched()
    {
        var issuerId = await SeedIssuerAsync(
            existingMode: EAdvanceTaxReceiptMode.OnPaymentMatch,
            notes: "Send a copy to the accountant");

        await _service.SetAdvanceTaxReceiptModeAsync(EAdvanceTaxReceiptMode.OnAnyPayment);

        var settings = await _context.BillingSettings.AsNoTracking().SingleAsync(b => b.ClientId == issuerId);
        settings.Notes.ShouldBe("Send a copy to the accountant");
        settings.DueDays.ShouldBe(14);
    }

    [Fact]
    public async Task SetAdvanceTaxReceiptMode_CalledTwice_KeepsSingleBillingSettingsRow()
    {
        var issuerId = await SeedIssuerAsync();

        await _service.SetAdvanceTaxReceiptModeAsync(EAdvanceTaxReceiptMode.Disabled);
        await _service.SetAdvanceTaxReceiptModeAsync(EAdvanceTaxReceiptMode.OnAnyPayment);

        _context.BillingSettings.Count(b => b.ClientId == issuerId).ShouldBe(1);
        (await ReadStoredModeAsync(issuerId)).ShouldBe(EAdvanceTaxReceiptMode.OnAnyPayment);
    }

    // ── Regression: the mode must not bleed through other update paths ──────

    [Theory]
    [InlineData(EAdvanceTaxReceiptMode.Disabled)]
    [InlineData(EAdvanceTaxReceiptMode.OnPaymentMatch)]
    [InlineData(EAdvanceTaxReceiptMode.OnAnyPayment)]
    public async Task UpdateBillingSettings_DefaultDto_DoesNotResetStoredMode(
        EAdvanceTaxReceiptMode storedMode)
    {
        // UpdateBillingSettingsAsync takes CreateBillingSettingsDto, which knows nothing
        // about the mode. Saving the billing form must therefore leave it alone.
        //
        // Why a Theory over EVERY value instead of a single seeded one: if the DTO ever
        // regrows the property without an initializer, it arrives as the CLR default
        // (Disabled = 0). A test seeded with Disabled would still pass, because the
        // bleed-through happens to write back exactly the value it asserts. Only a case
        // seeded with a non-zero mode catches that regression, so cover them all.
        var issuerId = await SeedIssuerAsync(existingMode: storedMode);

        await _service.UpdateBillingSettingsAsync(issuerId, new CreateBillingSettingsDto
        {
            DueDays = 30,
            DueDateCalculationType = EDueDateCalculationType.DaysFromIssue
        });

        (await ReadStoredModeAsync(issuerId)).ShouldBe(storedMode);
    }

    [Fact]
    public async Task UpdateClient_WithBillingSettingsPayload_DoesNotResetOnAnyPaymentMode()
    {
        // The company profile page saves through UpdateClientAsync. Same rule applies.
        var issuerId = await SeedIssuerAsync(existingMode: EAdvanceTaxReceiptMode.OnAnyPayment);

        await _service.UpdateClientAsync(issuerId, new UpdateClientDto
        {
            CompanyName = "Test Company s.r.o.",
            BillingSettings = new UpdateBillingSettingsDto { DueDays = 21 }
        });

        (await ReadStoredModeAsync(issuerId)).ShouldBe(EAdvanceTaxReceiptMode.OnAnyPayment);
    }

    [Fact]
    public async Task CreateClient_WithBillingSettingsPayload_KeepsEntityDefaultMode()
    {
        // CreateClientAsync maps CreateBillingSettingsDto with ZMapper, which pairs
        // properties BY NAME and needs no change in the service to pick a new one up.
        // The create path is therefore one more place where the mode could start coming
        // from the caller — a brand-new client must always land on the entity default.
        var created = await _service.CreateClientAsync(new CreateClientDto
        {
            CompanyName = "Brand New Customer s.r.o.",
            RegistrationNumber = "87654321",
            FetchFromAres = false,
            BillingSettings = new CreateBillingSettingsDto
            {
                DueDays = 30,
                DueDateCalculationType = EDueDateCalculationType.DaysFromIssue
            }
        });

        (await ReadStoredModeAsync(created.Id)).ShouldBe(EAdvanceTaxReceiptMode.OnPaymentMatch);
    }
}

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
/// Unit tests for CurrencyService.
/// Focuses on verifying the soft-delete behavior (IsActive = false instead of Remove).
/// Uses InMemoryDatabase with unique names for test isolation.
/// </summary>
public class CurrencyServiceTests : IDisposable
{
    private readonly TenantDbContext _context;
    private readonly MasterDbContext _masterContext;
    private readonly CurrencyService _service;
    private readonly ILogger<CurrencyService> _logger;

    public CurrencyServiceTests()
    {
        // Each test class gets its own in-memory tenant database to prevent cross-test contamination
        var options = new DbContextOptionsBuilder<TenantDbContext>()
            .UseInMemoryDatabase(databaseName: Guid.NewGuid().ToString())
            .Options;

        _context = new TenantDbContext(options);

        // Setup in-memory master database (required by multi-tenant service constructor)
        var masterOptions = new DbContextOptionsBuilder<MasterDbContext>()
            .UseInMemoryDatabase(databaseName: Guid.NewGuid().ToString())
            .Options;
        _masterContext = new MasterDbContext(masterOptions);

        // Mock ITenantResolver to simulate a tenant context — returns companyId 1L
        // so the service uses TenantDbContext (existing test behavior)
        var tenantResolver = Substitute.For<ITenantResolver>();
        tenantResolver.GetCurrentCompanyId().Returns(1L);

        _logger = Substitute.For<ILogger<CurrencyService>>();
        _service = new CurrencyService(_context, _masterContext, tenantResolver, _logger);

        SeedTestData();
    }

    public void Dispose()
    {
        _context.Database.EnsureDeleted();
        _context.Dispose();
        _masterContext.Database.EnsureDeleted();
        _masterContext.Dispose();
    }

    /// <summary>
    /// Seeds test currencies into master context — CurrencyService reads from master DB.
    /// Currencies are global/shared data, not tenant-specific.
    /// </summary>
    private void SeedTestData()
    {
        _masterContext.Currency.AddRange(
            new Currency
            {
                Id = 1, Code = "CZK", Name = "Czech Koruna", Symbol = "Kč",
                DecimalPlaces = 2, SortOrder = 1, IsActive = true
            },
            new Currency
            {
                Id = 2, Code = "EUR", Name = "Euro", Symbol = "€",
                DecimalPlaces = 2, SortOrder = 2, IsActive = true
            },
            new Currency
            {
                Id = 3, Code = "GBP", Name = "British Pound", Symbol = "£",
                DecimalPlaces = 2, SortOrder = 3, IsActive = false
            }
        );
        _masterContext.SaveChanges();
    }

    // --- Soft Delete Tests ---

    [Fact]
    public async Task DeleteCurrencyAsync_ShouldSoftDelete_SetIsActiveToFalse()
    {
        // Arrange — CZK (Id=1) is active
        var currencyBefore = await _masterContext.Currency.FindAsync(1L);
        currencyBefore!.IsActive.ShouldBeTrue();

        // Act — delete should set IsActive = false, NOT remove from database
        var result = await _service.DeleteCurrencyAsync(1);

        // Assert
        result.ShouldBeTrue();

        // Currency should still exist in master database but be inactive
        var currencyAfter = await _masterContext.Currency.FindAsync(1L);
        currencyAfter.ShouldNotBeNull();
        currencyAfter!.IsActive.ShouldBeFalse();
    }

    [Fact]
    public async Task DeleteCurrencyAsync_ShouldSoftDelete_EvenWhenUsedByInvoices()
    {
        // Arrange — add client and issuer first (InMemoryDb enforces IsRequired from DbContext)
        _context.Client.Add(new Client
        {
            Id = 10, CompanyName = "Test Client", RegistrationNumber = "REG010", IsIssuer = false, IsActive = true
        });
        await _context.SaveChangesAsync();
        _context.Client.Add(new Client
        {
            Id = 11, CompanyName = "Test Issuer", RegistrationNumber = "REG011", IsIssuer = true, IsActive = true
        });
        await _context.SaveChangesAsync();

        // Add invoice referencing the CZK currency
        _context.Invoice.Add(new Invoice
        {
            CurrencyId = 1, ClientId = 10, IssuerId = 11,
            Status = Domain.Enums.EInvoiceStatus.Draft,
            DocumentType = Domain.Enums.EDocumentType.Invoice,
            InvoiceItem = new List<InvoiceItem>()
        });
        await _context.SaveChangesAsync();

        // Act — soft delete should work even when currency is referenced
        var result = await _service.DeleteCurrencyAsync(1);

        // Assert — no exception, currency is deactivated
        result.ShouldBeTrue();
        var currency = await _masterContext.Currency.FindAsync(1L);
        currency!.IsActive.ShouldBeFalse();
    }

    [Fact]
    public async Task DeleteCurrencyAsync_ShouldReturnFalse_WhenCurrencyNotFound()
    {
        // Act — try to delete a non-existent currency
        var result = await _service.DeleteCurrencyAsync(999);

        // Assert
        result.ShouldBeFalse();
    }

    // --- GetActiveCurrenciesAsync Tests ---

    [Fact]
    public async Task GetActiveCurrenciesAsync_ShouldReturnOnlyActiveCurrencies()
    {
        // Act
        var result = await _service.GetActiveCurrenciesAsync();

        // Assert — GBP (Id=3) is inactive and should not appear
        result.Count.ShouldBe(2);
        result.ShouldContain(c => c.Code == "CZK");
        result.ShouldContain(c => c.Code == "EUR");
        result.ShouldNotContain(c => c.Code == "GBP");
    }

    [Fact]
    public async Task GetActiveCurrenciesAsync_ShouldExcludeSoftDeletedCurrencies()
    {
        // Arrange — soft-delete EUR
        await _service.DeleteCurrencyAsync(2);

        // Act
        var result = await _service.GetActiveCurrenciesAsync();

        // Assert — EUR should now be excluded
        result.Count.ShouldBe(1);
        result.ShouldContain(c => c.Code == "CZK");
        result.ShouldNotContain(c => c.Code == "EUR");
    }

    // --- Paged Queries with IsActive filter ---

    [Fact]
    public async Task GetCurrenciesPagedAsync_ShouldFilterByIsActive()
    {
        // Act — get only inactive currencies
        var result = await _service.GetCurrenciesPagedAsync(isActive: false);

        // Assert — only GBP is inactive
        result.Items.Count.ShouldBe(1);
        result.Items.First().Code.ShouldBe("GBP");
    }

    [Fact]
    public async Task GetCurrenciesPagedAsync_ShouldReturnAll_WhenNoIsActiveFilter()
    {
        // Act — no isActive filter shows all currencies
        var result = await _service.GetCurrenciesPagedAsync(isActive: null);

        // Assert — all 3 currencies returned
        result.Items.Count.ShouldBe(3);
    }
}

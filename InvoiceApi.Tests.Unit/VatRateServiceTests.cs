using InvoiceApi.Contracts.Dto.VatRate;
using InvoiceApi.Application.Service;
using InvoiceApi.Domain.Entities;
using InvoiceApi.Infrastructure.Data;
using InvoiceApi.Infrastructure.Service;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using NSubstitute;

namespace InvoiceApi.Tests.Unit;

/// <summary>
/// Unit tests for VatRateService
/// Tests business logic and validation rules for VAT rate management
/// </summary>
public class VatRateServiceTests : IDisposable
{
    private readonly TenantDbContext _context;
    private readonly MasterDbContext _masterContext;
    private readonly VatRateService _service;
    private readonly ILogger<VatRateService> _logger;

    public VatRateServiceTests()
    {
        // Setup in-memory tenant database for testing
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

        _logger = Substitute.For<ILogger<VatRateService>>();
        _service = new VatRateService(_context, _masterContext, tenantResolver, _logger);

        // Seed initial test data
        SeedTestData();
    }

    public void Dispose()
    {
        _context.Database.EnsureDeleted();
        _context.Dispose();
        _masterContext.Database.EnsureDeleted();
        _masterContext.Dispose();
    }

    private void SeedTestData()
    {
        var vatRates = new List<VatRate>
        {
            new VatRate
            {
                Id = 1,
                Name = "Standard 21%",
                Rate = 21.00m,
                ValidFrom = new DateTime(2013, 1, 1),
                ValidTo = null,
                IsReduced = false,
                IsDefault = true,
                IsActive = true
            },
            new VatRate
            {
                Id = 2,
                Name = "Reduced 12%",
                Rate = 12.00m,
                ValidFrom = new DateTime(2015, 1, 1),
                ValidTo = null,
                IsReduced = true,
                IsDefault = true,
                IsActive = true
            },
            new VatRate
            {
                Id = 3,
                Name = "Old Standard 20%",
                Rate = 20.00m,
                ValidFrom = new DateTime(2010, 1, 1),
                ValidTo = new DateTime(2012, 12, 31),
                IsReduced = false,
                IsDefault = false,
                IsActive = false
            }
        };

        _context.VatRate.AddRange(vatRates);
        _context.SaveChanges();
    }

    [Fact]
    public async Task GetAllVatRatesAsync_ReturnsOnlyActiveRates_WhenIncludeInactiveIsFalse()
    {
        // Act
        var result = await _service.GetAllVatRatesAsync(includeInactive: false);

        // Assert
        Assert.Equal(2, result.Count);
        Assert.All(result, rate => Assert.True(rate.IsActive));
    }

    [Fact]
    public async Task GetAllVatRatesAsync_ReturnsAllRates_WhenIncludeInactiveIsTrue()
    {
        // Act
        var result = await _service.GetAllVatRatesAsync(includeInactive: true);

        // Assert
        Assert.Equal(3, result.Count);
    }

    [Fact]
    public async Task GetVatRateByIdAsync_ReturnsCorrectRate_WhenRateExists()
    {
        // Act
        var result = await _service.GetVatRateByIdAsync(1);

        // Assert
        Assert.NotNull(result);
        Assert.Equal(1, result.Id);
        Assert.Equal("Standard 21%", result.Name);
        Assert.Equal(21.00m, result.Rate);
    }

    [Fact]
    public async Task GetVatRateByIdAsync_ReturnsNull_WhenRateDoesNotExist()
    {
        // Act
        var result = await _service.GetVatRateByIdAsync(999);

        // Assert
        Assert.Null(result);
    }

    [Fact]
    public async Task GetActiveVatRatesForDateAsync_ReturnsValidRates_ForCurrentDate()
    {
        // Act
        var result = await _service.GetActiveVatRatesForDateAsync(DateTime.UtcNow);

        // Assert
        Assert.Equal(2, result.Count);
        Assert.Contains(result, r => r.Rate == 21.00m);
        Assert.Contains(result, r => r.Rate == 12.00m);
    }

    [Fact]
    public async Task GetActiveVatRatesForDateAsync_ReturnsHistoricalRate_ForOldDate()
    {
        // Arrange - Add old rate back as active for this test
        var oldRate = await _context.VatRate.FindAsync(3L);
        oldRate!.IsActive = true;
        await _context.SaveChangesAsync();

        // Act
        var result = await _service.GetActiveVatRatesForDateAsync(new DateTime(2011, 6, 1));

        // Assert
        Assert.Contains(result, r => r.Rate == 20.00m);
    }

    [Fact]
    public async Task GetDefaultStandardRateAsync_ReturnsStandardRate()
    {
        // Act
        var result = await _service.GetDefaultStandardRateAsync();

        // Assert
        Assert.NotNull(result);
        Assert.Equal(21.00m, result.Rate);
        Assert.False(result.IsReduced);
        Assert.True(result.IsDefault);
    }

    [Fact]
    public async Task GetDefaultReducedRateAsync_ReturnsReducedRate()
    {
        // Act
        var result = await _service.GetDefaultReducedRateAsync();

        // Assert
        Assert.NotNull(result);
        Assert.Equal(12.00m, result.Rate);
        Assert.True(result.IsReduced);
        Assert.True(result.IsDefault);
    }

    [Fact]
    public async Task CreateVatRateAsync_CreatesNewRate_WithValidData()
    {
        // Arrange
        var createDto = new CreateVatRateDto
        {
            Name = "New Rate 15%",
            Rate = 15.00m,
            ValidFrom = DateTime.UtcNow,
            ValidTo = null,
            IsReduced = false,
            IsDefault = false,
            IsActive = true
        };

        // Act
        var result = await _service.CreateVatRateAsync(createDto);

        // Assert
        Assert.NotNull(result);
        Assert.Equal("New Rate 15%", result.Name);
        Assert.Equal(15.00m, result.Rate);

        // Verify it was saved to database
        var savedRate = await _context.VatRate.FindAsync(result.Id);
        Assert.NotNull(savedRate);
    }

    [Fact]
    public async Task CreateVatRateAsync_ThrowsException_WhenDefaultStandardRateAlreadyExists()
    {
        // Arrange
        var createDto = new CreateVatRateDto
        {
            Name = "Another Standard Default",
            Rate = 23.00m,
            ValidFrom = DateTime.UtcNow,
            ValidTo = null,
            IsReduced = false,
            IsDefault = true, // Trying to create another default standard
            IsActive = true
        };

        // Act & Assert
        var exception = await Assert.ThrowsAsync<InvalidOperationException>(
            () => _service.CreateVatRateAsync(createDto));

        Assert.Contains("default standard VAT rate already exists", exception.Message);
    }

    [Fact]
    public async Task CreateVatRateAsync_ThrowsException_WhenDefaultReducedRateAlreadyExists()
    {
        // Arrange
        var createDto = new CreateVatRateDto
        {
            Name = "Another Reduced Default",
            Rate = 10.00m,
            ValidFrom = DateTime.UtcNow,
            ValidTo = null,
            IsReduced = true,
            IsDefault = true, // Trying to create another default reduced
            IsActive = true
        };

        // Act & Assert
        var exception = await Assert.ThrowsAsync<InvalidOperationException>(
            () => _service.CreateVatRateAsync(createDto));

        Assert.Contains("default reduced VAT rate already exists", exception.Message);
    }

    [Fact]
    public async Task CreateVatRateAsync_ThrowsException_WhenValidToIsBeforeValidFrom()
    {
        // Arrange
        var createDto = new CreateVatRateDto
        {
            Name = "Invalid Date Range",
            Rate = 15.00m,
            ValidFrom = new DateTime(2025, 12, 31),
            ValidTo = new DateTime(2025, 1, 1), // Before ValidFrom
            IsReduced = false,
            IsDefault = false,
            IsActive = true
        };

        // Act & Assert
        var exception = await Assert.ThrowsAsync<InvalidOperationException>(
            () => _service.CreateVatRateAsync(createDto));

        Assert.Contains("ValidTo date must be after ValidFrom date", exception.Message);
    }

    [Fact]
    public async Task UpdateVatRateAsync_UpdatesRate_WithValidData()
    {
        // Arrange
        var updateDto = new UpdateVatRateDto
        {
            Name = "Updated Standard 21%",
            Rate = 21.00m,
            ValidFrom = new DateTime(2013, 1, 1),
            ValidTo = null,
            IsReduced = false,
            IsDefault = true,
            IsActive = true
        };

        // Act
        var result = await _service.UpdateVatRateAsync(1, updateDto);

        // Assert
        Assert.NotNull(result);
        Assert.Equal("Updated Standard 21%", result.Name);

        // Verify in database
        var savedRate = await _context.VatRate.FindAsync(1L);
        Assert.Equal("Updated Standard 21%", savedRate!.Name);
    }

    [Fact]
    public async Task UpdateVatRateAsync_ReturnsNull_WhenRateDoesNotExist()
    {
        // Arrange
        var updateDto = new UpdateVatRateDto
        {
            Name = "Non-existent",
            Rate = 15.00m,
            ValidFrom = DateTime.UtcNow,
            ValidTo = null,
            IsReduced = false,
            IsDefault = false,
            IsActive = true
        };

        // Act
        var result = await _service.UpdateVatRateAsync(999, updateDto);

        // Assert
        Assert.Null(result);
    }

    [Fact]
    public async Task DeleteVatRateAsync_SetsInactive_WhenRateNotUsedInInvoices()
    {
        // Act
        var result = await _service.DeleteVatRateAsync(3);

        // Assert
        Assert.True(result);

        // Verify it's marked inactive in database
        var rate = await _context.VatRate.FindAsync(3L);
        Assert.False(rate!.IsActive);
    }

    [Fact]
    public async Task DeleteVatRateAsync_ReturnsFalse_WhenRateDoesNotExist()
    {
        // Act
        var result = await _service.DeleteVatRateAsync(999);

        // Assert
        Assert.False(result);
    }

    [Fact]
    public async Task DeleteVatRateAsync_ThrowsException_WhenRateIsUsedInInvoices()
    {
        // Arrange - Create an invoice item using the VAT rate
        var invoice = new Invoice
        {
            DocumentType = Domain.Enums.EDocumentType.Invoice,
            Status = Domain.Enums.EInvoiceStatus.Draft,
            DocumentNumber = "TEST001",
            IssueDate = DateTime.UtcNow,
            DueDate = DateTime.UtcNow.AddDays(14),
            ClientId = 1,
            IssuerId = 1,
            CurrencyId = 1 // CZK
        };
        _context.Invoice.Add(invoice);
        await _context.SaveChangesAsync();

        var invoiceItem = new InvoiceItem
        {
            InvoiceId = invoice.Id,
            OrderIndex = 1,
            Description = "Test Item",
            Quantity = 1,
            Unit = "pcs",
            UnitPrice = 100m,
            VatRateId = 1, // Using VAT rate ID 1
            VatRatePercentage = 21m,
            TotalBeforeVat = 100m,
            VatAmount = 21m,
            TotalWithVat = 121m
        };
        _context.InvoiceItem.Add(invoiceItem);
        await _context.SaveChangesAsync();

        // Act & Assert
        var exception = await Assert.ThrowsAsync<InvalidOperationException>(
            () => _service.DeleteVatRateAsync(1));

        Assert.Contains("used in invoices", exception.Message);
    }

    [Fact]
    public async Task SetAsDefaultAsync_SetsRateAsDefault_AndUnsetsOldDefault()
    {
        // Arrange - Create a new standard rate
        var newRate = new VatRate
        {
            Name = "New Standard 23%",
            Rate = 23.00m,
            ValidFrom = DateTime.UtcNow,
            ValidTo = null,
            IsReduced = false,
            IsDefault = false,
            IsActive = true
        };
        _context.VatRate.Add(newRate);
        await _context.SaveChangesAsync();

        // Act
        var result = await _service.SetAsDefaultAsync(newRate.Id);

        // Assert
        Assert.NotNull(result);
        Assert.True(result.IsDefault);

        // Verify old default is now false
        var oldDefault = await _context.VatRate.FindAsync(1L);
        Assert.False(oldDefault!.IsDefault);
    }

    [Fact]
    public async Task SetAsDefaultAsync_ReturnsNull_WhenRateDoesNotExist()
    {
        // Act
        var result = await _service.SetAsDefaultAsync(999);

        // Assert
        Assert.Null(result);
    }

    [Fact]
    public async Task SetAsDefaultAsync_DoesNotAffectOtherType_WhenSettingDefault()
    {
        // Arrange - Create a new reduced rate
        var newRate = new VatRate
        {
            Name = "New Reduced 10%",
            Rate = 10.00m,
            ValidFrom = DateTime.UtcNow,
            ValidTo = null,
            IsReduced = true,
            IsDefault = false,
            IsActive = true
        };
        _context.VatRate.Add(newRate);
        await _context.SaveChangesAsync();

        // Act - Set new reduced as default
        await _service.SetAsDefaultAsync(newRate.Id);

        // Assert - Standard default should still be default
        var standardDefault = await _context.VatRate.FindAsync(1L);
        Assert.True(standardDefault!.IsDefault);
        Assert.False(standardDefault.IsReduced);
    }
}

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
/// Unit tests for CurrencyService.ConvertToCzkAsync and GetExchangeRateAsync.
///
/// Uses InMemoryDatabase seeded with exchange rate records.
/// CnbExchangeRateProvider is mocked (no HTTP calls in tests).
/// </summary>
public class ExchangeRateConversionTests : IDisposable
{
    private readonly TenantDbContext _tenantContext;
    private readonly MasterDbContext _masterContext;
    private readonly CurrencyService _service;
    private readonly IExchangeRateProvider _cnbProvider;
    private readonly ITenantResolver _tenantResolver;
    private readonly ILogger<CurrencyService> _logger;

    public ExchangeRateConversionTests()
    {
        var tenantOptions = new DbContextOptionsBuilder<TenantDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;
        _tenantContext = new TenantDbContext(tenantOptions);

        var masterOptions = new DbContextOptionsBuilder<MasterDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;
        _masterContext = new MasterDbContext(masterOptions);

        _tenantResolver = Substitute.For<ITenantResolver>();
        _tenantResolver.GetCurrentCompanyId().Returns((long?)null);

        _cnbProvider = Substitute.For<IExchangeRateProvider>();
        _logger = Substitute.For<ILogger<CurrencyService>>();

        _service = new CurrencyService(
            _tenantContext,
            _masterContext,
            _tenantResolver,
            _cnbProvider,
            _logger);

        SeedExchangeRates();
    }

    public void Dispose()
    {
        _tenantContext.Dispose();
        _masterContext.Dispose();
    }

    /// <summary>Seeds test exchange rates into tenant context.</summary>
    private void SeedExchangeRates()
    {
        // EUR rate for a specific weekday
        _tenantContext.ExchangeRate.Add(new ExchangeRate
        {
            CurrencyCode = "EUR",
            ValidFrom = new DateOnly(2025, 5, 2),   // Friday
            Rate = 25.255m,
            Amount = 1,
            Source = "CNB"
        });

        // EUR rate for the previous week
        _tenantContext.ExchangeRate.Add(new ExchangeRate
        {
            CurrencyCode = "EUR",
            ValidFrom = new DateOnly(2025, 4, 25),  // older Friday
            Rate = 25.100m,
            Amount = 1,
            Source = "CNB"
        });

        // JPY rate with Amount = 100
        _tenantContext.ExchangeRate.Add(new ExchangeRate
        {
            CurrencyCode = "JPY",
            ValidFrom = new DateOnly(2025, 5, 2),
            Rate = 15.123m,
            Amount = 100,
            Source = "CNB"
        });

        // USD rate
        _tenantContext.ExchangeRate.Add(new ExchangeRate
        {
            CurrencyCode = "USD",
            ValidFrom = new DateOnly(2025, 5, 2),
            Rate = 23.140m,
            Amount = 1,
            Source = "CNB"
        });

        _tenantContext.SaveChanges();
    }

    // ─── CZK passthrough ─────────────────────────────────────────────────────

    [Fact]
    public async Task ConvertToCzkAsync_CurrencyCzk_ReturnsAmountUnchanged()
    {
        // Arrange
        var amount = 12345.67m;

        // Act
        var result = await _service.ConvertToCzkAsync(amount, "CZK", new DateOnly(2025, 5, 2));

        // Assert — CZK is always 1:1
        result.ShouldBe(amount);
    }

    [Fact]
    public async Task ConvertToCzkAsync_CurrencyCzkLowercase_ReturnsAmountUnchanged()
    {
        // Arrange
        var result = await _service.ConvertToCzkAsync(1000m, "czk", new DateOnly(2025, 5, 2));

        // Assert
        result.ShouldBe(1000m);
    }

    // ─── EUR → CZK conversion ─────────────────────────────────────────────────

    [Fact]
    public async Task ConvertToCzkAsync_EurOnExactDate_UsesCorrectRate()
    {
        // Arrange — rate on 2025-05-02 is 25.255 CZK/EUR
        // 100 EUR * 25.255 = 2525.50 CZK
        var expectedCzk = 100m * 25.255m; // = 2525.50

        // Act
        var result = await _service.ConvertToCzkAsync(100m, "EUR", new DateOnly(2025, 5, 2));

        // Assert
        result.ShouldBe(Math.Round(expectedCzk, 2, MidpointRounding.AwayFromZero));
    }

    [Fact]
    public async Task ConvertToCzkAsync_EurOnWeekend_FallsBackToPreviousRate()
    {
        // Arrange — Saturday (2025-05-03) has no published rate;
        // must fall back to Friday 2025-05-02 (25.255 CZK/EUR)
        var expectedCzk = 1m * 25.255m; // 1 EUR

        // Act — request rate for Saturday
        var result = await _service.ConvertToCzkAsync(1m, "EUR", new DateOnly(2025, 5, 3));

        // Assert — uses Friday's rate (fallback)
        result.ShouldBe(Math.Round(expectedCzk, 2, MidpointRounding.AwayFromZero));
    }

    [Fact]
    public async Task ConvertToCzkAsync_EurOlderDate_UsesMostRecentPriorRate()
    {
        // Arrange — date falls between the two EUR records (2025-04-26).
        // The prior record is 2025-04-25 (25.100 CZK/EUR).
        var expected = 10m * 25.100m;

        // Act
        var result = await _service.ConvertToCzkAsync(10m, "EUR", new DateOnly(2025, 4, 26));

        // Assert — uses the 2025-04-25 rate
        result.ShouldBe(Math.Round(expected, 2, MidpointRounding.AwayFromZero));
    }

    // ─── JPY conversion (Amount = 100) ───────────────────────────────────────

    [Fact]
    public async Task ConvertToCzkAsync_JpyWithAmountUnit_CalculatesCorrectly()
    {
        // Arrange — JPY rate: 15.123 CZK per 100 JPY → 0.15123 CZK/JPY
        // 1000 JPY = 1000 * (15.123 / 100) = 151.23 CZK
        var expected = 1000m * (15.123m / 100m);

        // Act
        var result = await _service.ConvertToCzkAsync(1000m, "JPY", new DateOnly(2025, 5, 2));

        // Assert
        result.ShouldBe(Math.Round(expected, 2, MidpointRounding.AwayFromZero));
    }

    // ─── Missing rate — exception ─────────────────────────────────────────────

    [Fact]
    public async Task ConvertToCzkAsync_CurrencyNotInDatabase_ThrowsExchangeRateNotFoundException()
    {
        // Arrange — GBP has no records in test seed
        var act = async () =>
            await _service.ConvertToCzkAsync(1m, "GBP", new DateOnly(2025, 5, 2));

        // Assert
        await act.ShouldThrowAsync<ExchangeRateNotFoundException>();
    }

    [Fact]
    public async Task ConvertToCzkAsync_EurBeforeAnyRecord_ThrowsExchangeRateNotFoundException()
    {
        // Arrange — date is before the oldest EUR record (2025-04-25)
        var act = async () =>
            await _service.ConvertToCzkAsync(1m, "EUR", new DateOnly(2025, 1, 1));

        // Assert
        var ex = await act.ShouldThrowAsync<ExchangeRateNotFoundException>();
        ex.CurrencyCode.ShouldBe("EUR");
        ex.Date.ShouldBe(new DateOnly(2025, 1, 1));
    }

    // ─── GetExchangeRateAsync ─────────────────────────────────────────────────

    [Fact]
    public async Task GetExchangeRateAsync_CurrencyEurExactDate_ReturnsCorrectDto()
    {
        // Act
        var dto = await _service.GetExchangeRateAsync("EUR", new DateOnly(2025, 5, 2));

        // Assert
        dto.ShouldNotBeNull();
        dto!.CurrencyCode.ShouldBe("EUR");
        dto.Rate.ShouldBe(25.255m);
        dto.Amount.ShouldBe(1);
        dto.ValidFrom.ShouldBe(new DateOnly(2025, 5, 2));
        // Computed property: EffectiveRate = 25.255 / 1 = 25.255
        dto.EffectiveRate.ShouldBe(25.255m);
    }

    [Fact]
    public async Task GetExchangeRateAsync_CurrencyCzk_ReturnsNull()
    {
        // CZK has no stored rate — always 1:1
        var dto = await _service.GetExchangeRateAsync("CZK", new DateOnly(2025, 5, 2));

        dto.ShouldBeNull();
    }

    [Fact]
    public async Task GetExchangeRateAsync_CurrencyNotFound_ReturnsNull()
    {
        // GBP not seeded
        var dto = await _service.GetExchangeRateAsync("GBP", new DateOnly(2025, 5, 2));

        dto.ShouldBeNull();
    }
}

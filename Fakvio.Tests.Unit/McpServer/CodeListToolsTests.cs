using System.Text.Json;
using Fakvio.Contracts.Dto.Currency;
using Fakvio.McpServer.Client;
using Fakvio.McpServer.Tools;
using NSubstitute;
using NSubstitute.ExceptionExtensions;
using Shouldly;

namespace Fakvio.Tests.Unit.McpServer;

/// <summary>
/// Tests for <see cref="CodeListTools"/> — small lookup-table tools (N2.3).
/// </summary>
public class CodeListToolsTests
{
    private readonly IFakvioApiClient _api = Substitute.For<IFakvioApiClient>();

    [Fact]
    public async Task ListCurrencies_ReturnsCodeNameSymbol()
    {
        _api.GetActiveCurrenciesAsync(Arg.Any<CancellationToken>()).Returns(new List<CurrencyDto>
        {
            new() { Id = 1, Code = "CZK", Name = "Czech Koruna", Symbol = "Kč", SortOrder = 0 },
            new() { Id = 2, Code = "EUR", Name = "Euro", Symbol = "€", SortOrder = 1 }
        });

        var json = await CodeListTools.ListCurrencies(_api);

        var items = JsonDocument.Parse(json).RootElement;
        items.GetArrayLength().ShouldBe(2);
        items[0].GetProperty("code").GetString().ShouldBe("CZK");
        items[1].GetProperty("code").GetString().ShouldBe("EUR");
        items[1].GetProperty("symbol").GetString().ShouldBe("€");
        // Narrowed output — no SortOrder/DisplayFormat/IsActive leaking through.
        items[0].TryGetProperty("sortOrder", out _).ShouldBeFalse();
    }

    [Fact]
    public async Task ListCurrencies_OnApiFailure_ReturnsSanitizedError()
    {
        _api.GetActiveCurrenciesAsync(Arg.Any<CancellationToken>())
            .ThrowsAsync(new InvalidOperationException("boom"));

        var json = await CodeListTools.ListCurrencies(_api);

        var root = JsonDocument.Parse(json).RootElement;
        root.GetProperty("error").GetString().ShouldBe("internal_error");
    }

    [Fact]
    public async Task GetExchangeRate_ReturnsRateAndPerUnitRate()
    {
        _api.GetExchangeRateAsync("HUF", new DateOnly(2026, 10, 2), Arg.Any<CancellationToken>()).Returns(
            new Fakvio.Contracts.Dto.ExchangeRate.ExchangeRateDto
            {
                CurrencyCode = "HUF", Amount = 100, Rate = 6.660m, RatePerUnit = 0.0666m, ValidFor = new DateOnly(2026, 10, 1)
            });

        var json = await CodeListTools.GetExchangeRate(_api, "HUF", new DateOnly(2026, 10, 2));

        var root = JsonDocument.Parse(json).RootElement;
        root.GetProperty("amount").GetInt32().ShouldBe(100);
        root.GetProperty("ratePerUnit").GetDecimal().ShouldBe(0.0666m);
        root.GetProperty("validFor").GetString().ShouldBe("2026-10-01");
    }

    [Theory]
    [InlineData("CZK")]
    [InlineData("")]
    [InlineData("EURO")]
    public async Task GetExchangeRate_InvalidCurrency_ReturnsErrorWithoutCallingApi(string currency)
    {
        var json = await CodeListTools.GetExchangeRate(_api, currency);

        JsonDocument.Parse(json).RootElement.TryGetProperty("error", out _).ShouldBeTrue();
        await _api.DidNotReceive().GetExchangeRateAsync(Arg.Any<string>(), Arg.Any<DateOnly?>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task GetExchangeRate_Unavailable_ReturnsClearError()
    {
        _api.GetExchangeRateAsync("USD", Arg.Any<DateOnly?>(), Arg.Any<CancellationToken>()).Returns((Fakvio.Contracts.Dto.ExchangeRate.ExchangeRateDto?)null);

        var json = await CodeListTools.GetExchangeRate(_api, "usd");

        JsonDocument.Parse(json).RootElement.GetProperty("error").GetString().ShouldContain("USD");
    }
}

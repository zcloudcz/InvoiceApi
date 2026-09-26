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
}

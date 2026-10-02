using System.Net;
using System.Net.Http.Json;
using Fakvio.Contracts.Dto.ExchangeRate;
using Fakvio.Tests.Integration.Fixtures;
using Fakvio.Tests.Integration.Helpers;
using Shouldly;

namespace Fakvio.Tests.Integration;

/// <summary>
/// ČNB exchange rates (DEVGUIDE §4.17): /api/exchange-rate is a master-only path (no tenant context needed),
/// readable by any authenticated user, backfill is SysAdmin only. The ČNB client is a canned fake (see factory).
/// </summary>
public class ExchangeRateEndpointTests : IClassFixture<FakvioFactory>
{
    private readonly FakvioFactory _factory;

    public ExchangeRateEndpointTests(FakvioFactory factory)
    {
        _factory = factory;
        _factory.InitializeDatabase();
    }

    private async Task<HttpClient> SysAdminAsync()
    {
        var client = _factory.CreateClient();
        AuthHelper.SetAuthToken(client, (await AuthHelper.LoginAsSysAdminAsync(client)).Token);
        return client; // deliberately no impersonation: master-only path
    }

    [Fact]
    public async Task Get_Unauthenticated_Returns401() =>
        (await _factory.CreateClient().GetAsync("/api/exchange-rate?currency=EUR")).StatusCode.ShouldBe(HttpStatusCode.Unauthorized);

    [Fact]
    public async Task Get_Weekend_ReturnsLastPublishedFixing_WithoutTenantContext()
    {
        var client = await SysAdminAsync();

        var rate = await client.GetFromJsonAsync<ExchangeRateDto>("/api/exchange-rate?currency=eur&date=2026-10-03");

        rate!.CurrencyCode.ShouldBe("EUR");
        rate.ValidFor.ShouldBe(new DateOnly(2026, 10, 1));
        rate.RatePerUnit.ShouldBe(24.465m);
    }

    [Fact]
    public async Task Get_PerHundredCurrency_ExposesAmountAndPerUnitRate()
    {
        var client = await SysAdminAsync();

        var rate = await client.GetFromJsonAsync<ExchangeRateDto>("/api/exchange-rate?currency=HUF&date=2026-10-01");

        rate!.Amount.ShouldBe(100);
        rate.Rate.ShouldBe(6.660m);
        rate.RatePerUnit.ShouldBe(0.0666m);
    }

    [Theory]
    [InlineData("CZK", HttpStatusCode.BadRequest)]
    [InlineData("", HttpStatusCode.BadRequest)]
    [InlineData("EURO", HttpStatusCode.BadRequest)]
    [InlineData("ZZZ", HttpStatusCode.NotFound)]
    public async Task Get_InvalidOrUnknownCurrency_ReturnsClientError(string currency, HttpStatusCode expected)
    {
        var client = await SysAdminAsync();

        (await client.GetAsync($"/api/exchange-rate?currency={currency}&date=2026-10-01")).StatusCode.ShouldBe(expected);
    }

    [Fact]
    public async Task Backfill_SysAdmin_Succeeds_AndRejectsBadRange()
    {
        var client = await SysAdminAsync();

        var ok = await client.PostAsJsonAsync("/api/exchange-rate/backfill",
            new ExchangeRateBackfillDto { From = new DateOnly(2026, 9, 28), To = new DateOnly(2026, 10, 2) });
        ok.StatusCode.ShouldBe(HttpStatusCode.OK);

        var inverted = await client.PostAsJsonAsync("/api/exchange-rate/backfill",
            new ExchangeRateBackfillDto { From = new DateOnly(2026, 10, 2), To = new DateOnly(2026, 9, 28) });
        inverted.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task Backfill_Unauthenticated_Returns401() =>
        (await _factory.CreateClient().PostAsJsonAsync("/api/exchange-rate/backfill",
            new ExchangeRateBackfillDto { From = new DateOnly(2026, 9, 28), To = new DateOnly(2026, 10, 2) }))
        .StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
}

using System.Net;
using Fakvio.Application.Service;
using Fakvio.Contracts.Dto.ExchangeRate;
using Fakvio.Contracts.Dto.Invoice;
using Fakvio.Domain.Entities;
using Fakvio.Domain.Enums;
using Fakvio.Infrastructure.Data;
using Fakvio.Infrastructure.Service;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging;
using NSubstitute;
using Shouldly;

namespace Fakvio.Tests.Unit;

/// <summary>
/// ČNB exchange rates (DEVGUIDE §4.17): file parser, Master cache + on-demand fetch with
/// "last fixing on or before the date" fallback, sync cycle, worker schedule, rate assignment on invoices,
/// CZK conversion and the CZK VAT block on the PDF.
/// </summary>
public class ExchangeRateTests : IDisposable
{
    // Real ČNB file of 1 Oct 2026 (trimmed): decimal comma, "množství" 100 for HUF/JPY, 1000 for IDR.
    private const string Sample =
        "01.10.2026 #189\r\nzemě|měna|množství|kód|kurz\r\n" +
        "EMU|euro|1|EUR|24,465\r\nJaponsko|jen|100|JPY|13,706\r\nMaďarsko|forint|100|HUF|6,660\r\n" +
        "Indonesie|rupie|1000|IDR|1,207\r\nUSA|dolar|1|USD|21,655\r\n";

    private readonly MasterDbContext _master = new(new DbContextOptionsBuilder<MasterDbContext>()
        .UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);
    private readonly ICnbExchangeRateClient _cnb = Substitute.For<ICnbExchangeRateClient>();
    private readonly ExchangeRateService _service;

    public ExchangeRateTests()
    {
        _service = new ExchangeRateService(_master, _cnb, new MemoryCache(new MemoryCacheOptions()),
            Substitute.For<ILogger<ExchangeRateService>>());
    }

    public void Dispose() => _master.Dispose();

    private static CnbDailyRates Daily(DateOnly validFor, decimal eur = 24.465m) => new(validFor,
        [new CnbRateRow("EUR", 1, eur), new CnbRateRow("HUF", 100, 6.660m)]);

    // ── parser ───────────────────────────────────────────────────────────────

    [Fact]
    public void Parser_ReadsDateAndRows_WithAmountAndDecimalComma()
    {
        var parsed = CnbExchangeRateClient.Parse(Sample);

        parsed.ValidFor.ShouldBe(new DateOnly(2026, 10, 1));
        parsed.Rows.Count.ShouldBe(5);
        parsed.Rows.Single(r => r.CurrencyCode == "EUR").ShouldBe(new CnbRateRow("EUR", 1, 24.465m));
        parsed.Rows.Single(r => r.CurrencyCode == "HUF").ShouldBe(new CnbRateRow("HUF", 100, 6.660m));
        parsed.Rows.Single(r => r.CurrencyCode == "JPY").Amount.ShouldBe(100);
        parsed.Rows.Single(r => r.CurrencyCode == "IDR").Amount.ShouldBe(1000);
    }

    [Theory]
    [InlineData("")]
    [InlineData("garbage\r\nheader\r\nEMU|euro|1|EUR|24,465")]            // header is not "dd.MM.yyyy #n"
    [InlineData("01.10.2026 #1\r\nheader\r\n")]                            // no rows
    [InlineData("01.10.2026 #1\r\nheader\r\nEMU|euro|1|EURO|24,465\r\nx|y|0|USD|1,0\r\nx|y|1|USD|abc")] // all lines malformed
    public void Parser_ThrowsOnUnusableFile(string body) =>
        Should.Throw<InvalidOperationException>(() => CnbExchangeRateClient.Parse(body));

    [Fact]
    public void Parser_SkipsMalformedLinesButKeepsGoodOnes()
    {
        var parsed = CnbExchangeRateClient.Parse("01.10.2026 #1\r\nheader\r\nbad line\r\nEMU|euro|1|EUR|24,465\r\n");
        parsed.Rows.Select(r => r.CurrencyCode).ShouldBe(["EUR"]);
    }

    [Fact]
    public async Task Client_RequestsDateInCnbFormat_AndParsesBody()
    {
        var handler = new StubHandler(HttpStatusCode.OK, Sample);
        var client = new CnbExchangeRateClient(new HttpClient(handler));

        var result = await client.GetDailyRatesAsync(new DateOnly(2026, 10, 2));

        handler.LastUrl!.ShouldEndWith("denni_kurz.txt?date=02.10.2026");
        result.ValidFor.ShouldBe(new DateOnly(2026, 10, 1)); // the file's own date wins over the requested one
    }

    [Fact]
    public async Task Client_HttpError_BecomesInvalidOperation()
    {
        var client = new CnbExchangeRateClient(new HttpClient(new StubHandler(HttpStatusCode.ServiceUnavailable, "")));
        await Should.ThrowAsync<InvalidOperationException>(() => client.GetDailyRatesAsync(new DateOnly(2026, 10, 2)));
    }

    private sealed class StubHandler(HttpStatusCode status, string body) : HttpMessageHandler
    {
        public string? LastUrl { get; private set; }
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            LastUrl = request.RequestUri!.ToString();
            return Task.FromResult(new HttpResponseMessage(status) { Content = new StringContent(body) });
        }
    }

    // ── lookup + fallback ────────────────────────────────────────────────────

    [Fact]
    public async Task GetRate_ExactDayStored_DoesNotCallCnb()
    {
        _master.ExchangeRate.Add(new ExchangeRate { CurrencyCode = "EUR", Amount = 1, Rate = 24.465m, ValidFor = new DateOnly(2026, 10, 1) });
        await _master.SaveChangesAsync();

        var rate = await _service.GetRateAsync("eur", new DateOnly(2026, 10, 1));

        rate!.RatePerUnit.ShouldBe(24.465m);
        rate.ValidFor.ShouldBe(new DateOnly(2026, 10, 1));
        await _cnb.DidNotReceive().GetDailyRatesAsync(Arg.Any<DateOnly>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task GetRate_Weekend_UsesLastPublishedFixing_FromCnbFallback()
    {
        // Sat 3 Oct 2026: ČNB answers with Friday 2 Oct's fixing; nothing stored yet.
        _cnb.GetDailyRatesAsync(new DateOnly(2026, 10, 3), Arg.Any<CancellationToken>())
            .Returns(Daily(new DateOnly(2026, 10, 2), 24.5m));

        var rate = await _service.GetRateAsync("EUR", new DateOnly(2026, 10, 3));

        rate!.ValidFor.ShouldBe(new DateOnly(2026, 10, 2));
        rate.RatePerUnit.ShouldBe(24.5m);
        (await _master.ExchangeRate.CountAsync()).ShouldBe(2); // stored for next time (EUR + HUF)
    }

    [Fact]
    public async Task GetRate_PreviousBusinessDayStored_StillAsksCnb_ThenUsesNewerFixing()
    {
        // Mon 5 Oct: Friday is stored, but Monday's fixing exists at ČNB — a stale "previous day" must not win.
        _master.ExchangeRate.Add(new ExchangeRate { CurrencyCode = "EUR", Amount = 1, Rate = 24.0m, ValidFor = new DateOnly(2026, 10, 2) });
        await _master.SaveChangesAsync();
        _cnb.GetDailyRatesAsync(new DateOnly(2026, 10, 5), Arg.Any<CancellationToken>())
            .Returns(Daily(new DateOnly(2026, 10, 5), 24.9m));

        var rate = await _service.GetRateAsync("EUR", new DateOnly(2026, 10, 5));

        rate!.ValidFor.ShouldBe(new DateOnly(2026, 10, 5));
        rate.RatePerUnit.ShouldBe(24.9m);
    }

    [Fact]
    public async Task GetRate_PerHundredCurrency_ReturnsPerUnitRate()
    {
        _cnb.GetDailyRatesAsync(Arg.Any<DateOnly>(), Arg.Any<CancellationToken>()).Returns(Daily(new DateOnly(2026, 10, 1)));

        var rate = await _service.GetRateAsync("HUF", new DateOnly(2026, 10, 1));

        rate!.Amount.ShouldBe(100);
        rate.Rate.ShouldBe(6.660m);
        rate.RatePerUnit.ShouldBe(0.0666m);
    }

    [Fact]
    public async Task GetRate_CnbDown_UsesRecentStoredRow_ButNotAnAncientOne()
    {
        _cnb.GetDailyRatesAsync(Arg.Any<DateOnly>(), Arg.Any<CancellationToken>())
            .Returns<CnbDailyRates>(_ => throw new InvalidOperationException("down"));
        _master.ExchangeRate.Add(new ExchangeRate { CurrencyCode = "EUR", Amount = 1, Rate = 24.0m, ValidFor = new DateOnly(2026, 10, 2) });
        await _master.SaveChangesAsync();

        (await _service.GetRateAsync("EUR", new DateOnly(2026, 10, 4)))!.ValidFor.ShouldBe(new DateOnly(2026, 10, 2));
        (await _service.GetRateAsync("EUR", new DateOnly(2026, 11, 20))).ShouldBeNull(); // too stale to be "the last fixing"
        (await _service.GetRateAsync("USD", new DateOnly(2026, 10, 2))).ShouldBeNull();   // nothing at all
    }

    [Fact]
    public async Task GetRate_OutageFallbackAnswer_IsNotCached()
    {
        var day = new DateOnly(2026, 9, 25);
        _master.ExchangeRate.Add(new ExchangeRate { CurrencyCode = "EUR", Amount = 1, Rate = 24.0m, ValidFor = new DateOnly(2026, 9, 24) });
        await _master.SaveChangesAsync();
        _cnb.GetDailyRatesAsync(day, Arg.Any<CancellationToken>()).Returns<CnbDailyRates>(_ => throw new InvalidOperationException("down"));
        (await _service.GetRateAsync("EUR", day))!.ValidFor.ShouldBe(new DateOnly(2026, 9, 24)); // outage fallback

        _cnb.GetDailyRatesAsync(day, Arg.Any<CancellationToken>()).Returns(Daily(day, 24.7m)); // ČNB is back

        (await _service.GetRateAsync("EUR", day))!.RatePerUnit.ShouldBe(24.7m); // not served from a cached fallback
    }

    [Theory]
    [InlineData("CZK")]
    [InlineData("")]
    [InlineData("EURO")]
    [InlineData("E1R")]
    public async Task GetRate_CzkOrInvalidCode_ReturnsNullWithoutCallingCnb(string code)
    {
        (await _service.GetRateAsync(code, new DateOnly(2026, 10, 1))).ShouldBeNull();
        await _cnb.DidNotReceive().GetDailyRatesAsync(Arg.Any<DateOnly>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task FetchAndStore_IsIdempotent()
    {
        _cnb.GetDailyRatesAsync(Arg.Any<DateOnly>(), Arg.Any<CancellationToken>()).Returns(Daily(new DateOnly(2026, 10, 1)));

        await _service.FetchAndStoreAsync(new DateOnly(2026, 10, 1));
        _cnb.GetDailyRatesAsync(Arg.Any<DateOnly>(), Arg.Any<CancellationToken>()).Returns(Daily(new DateOnly(2026, 10, 1), 24.5m)); // correction
        await _service.FetchAndStoreAsync(new DateOnly(2026, 10, 1));

        (await _master.ExchangeRate.CountAsync()).ShouldBe(2);
        (await _master.ExchangeRate.SingleAsync(r => r.CurrencyCode == "EUR")).Rate.ShouldBe(24.5m);
    }

    // ── sync cycle + worker schedule ─────────────────────────────────────────

    [Fact]
    public async Task Backfill_SkipsWeekends_AndRejectsBadRanges()
    {
        var rates = Substitute.For<IExchangeRateService>();
        var sync = new ExchangeRateSyncService(rates, _master, Substitute.For<ILogger<ExchangeRateSyncService>>());

        // Thu 1 Oct .. Tue 6 Oct 2026 = Thu, Fri, (Sat, Sun), Mon, Tue = 4 weekdays
        (await sync.BackfillAsync(new DateOnly(2026, 10, 1), new DateOnly(2026, 10, 6))).ShouldBe(4);
        await rates.Received(4).FetchAndStoreAsync(Arg.Any<DateOnly>(), Arg.Any<CancellationToken>());

        await Should.ThrowAsync<ArgumentException>(() => sync.BackfillAsync(new DateOnly(2026, 10, 6), new DateOnly(2026, 10, 1)));
        await Should.ThrowAsync<ArgumentException>(() => sync.BackfillAsync(new DateOnly(2025, 1, 1), new DateOnly(2026, 10, 1)));
    }

    [Fact]
    public async Task RunCycle_FreshDatabase_FetchesAtMostThirtyDaysOfHistory()
    {
        var rates = Substitute.For<IExchangeRateService>();
        var sync = new ExchangeRateSyncService(rates, _master, Substitute.For<ILogger<ExchangeRateSyncService>>());

        var count = await sync.RunCycleAsync();

        count.ShouldBeInRange(20, 23); // ~31 calendar days of weekdays
    }

    [Fact]
    public async Task RunCycle_UpToDate_RefetchesOnlyTheNewestDay()
    {
        var today = ExchangeRateWorker.PragueToday();
        _master.ExchangeRate.Add(new ExchangeRate { CurrencyCode = "EUR", Amount = 1, Rate = 24m, ValidFor = today });
        await _master.SaveChangesAsync();
        var rates = Substitute.For<IExchangeRateService>();
        var sync = new ExchangeRateSyncService(rates, _master, Substitute.For<ILogger<ExchangeRateSyncService>>());

        var count = await sync.RunCycleAsync();

        count.ShouldBeLessThanOrEqualTo(1); // today (0 on a weekend)
    }

    [Theory]
    [InlineData("2026-10-02T10:00:00", "04:45:00")]          // before 14:45 -> same day
    [InlineData("2026-10-02T14:45:00", "1.00:00:00")]        // exactly at it -> tomorrow
    [InlineData("2026-10-02T16:00:00", "22:45:00")]          // after -> tomorrow 14:45
    public void Worker_DelayUntilNextRun_IsNext1445Prague(string now, string expected) =>
        ExchangeRateWorker.DelayUntilNextRun(new DateTimeOffset(DateTime.Parse(now), TimeSpan.FromHours(2)))
            .ShouldBe(TimeSpan.Parse(expected));

    // ── invoice rate assignment ──────────────────────────────────────────────

    private (InvoiceService Svc, TenantDbContext Ctx, IExchangeRateService Rates) NewInvoiceService()
    {
        var ctx = new TenantDbContext(new DbContextOptionsBuilder<TenantDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);
        ctx.Currency.AddRange(
            new Currency { Id = 1, Code = "CZK", Name = "Koruna", Symbol = "Kc", DecimalPlaces = 2, SortOrder = 1, IsActive = true },
            new Currency { Id = 2, Code = "EUR", Name = "Euro", Symbol = "EUR", DecimalPlaces = 2, SortOrder = 2, IsActive = true });
        ctx.Client.Add(new Client { Id = 1, CompanyName = "Issuer", RegistrationNumber = "1", IsIssuer = true, IsActive = true });
        var client = new Client { Id = 10, CompanyName = "Customer", RegistrationNumber = "2", IsActive = true };
        client.Address.Add(new Address { ClientId = 10, Country = "CZ", IsPrimary = true, Street = "s", City = "c", PostalCode = "1" });
        ctx.Client.Add(client);
        ctx.SaveChanges();

        var seq = 0;
        var numbers = Substitute.For<INumberSequenceService>();
        numbers.GenerateNextNumberForDocumentTypeAsync(
                Arg.Any<EDocumentType>(), Arg.Any<DateTime>(), Arg.Any<string?>(), Arg.Any<string?>(), Arg.Any<CancellationToken>())
            .Returns(_ => $"2026{++seq:D4}");
        var resolver = Substitute.For<ITenantResolver>();
        resolver.GetCurrentCompanyId().Returns(77L);

        var rates = Substitute.For<IExchangeRateService>();
        var svc = new InvoiceService(ctx, _master, resolver, numbers, Substitute.For<ITenantReadinessService>(),
            Substitute.For<ILogger<InvoiceService>>(), exchangeRateService: rates);
        return (svc, ctx, rates);
    }

    private static CreateInvoiceDto InvoiceDto(long currencyId, DateTime? duzp = null, decimal? manualRate = null,
        EDocumentType type = EDocumentType.Invoice, long? original = null) => new()
    {
        DocumentType = type, ClientId = 10, IssuerId = 1, CurrencyId = currencyId, OriginalInvoiceId = original,
        IssueDate = new DateTime(2026, 10, 5, 0, 0, 0, DateTimeKind.Utc),
        TaxableSupplyDate = duzp ?? new DateTime(2026, 10, 3, 0, 0, 0, DateTimeKind.Utc), // a Saturday
        ExchangeRate = manualRate,
        InvoiceItem = [new() { OrderIndex = 1, Description = "x", Quantity = 1, Unit = "ks", UnitPrice = 100, VatRatePercentage = 0 }]
    };

    [Fact]
    public async Task Complete_ForeignInvoice_GetsCnbRateValidForDuzp()
    {
        var (svc, _, rates) = NewInvoiceService();
        rates.GetRateAsync("EUR", new DateOnly(2026, 10, 3), Arg.Any<CancellationToken>())
            .Returns(new ExchangeRateDto { CurrencyCode = "EUR", Amount = 1, Rate = 24.465m, RatePerUnit = 24.465m, ValidFor = new DateOnly(2026, 10, 2) });

        var created = await svc.CreateInvoiceAsync(InvoiceDto(currencyId: 2));
        created.ExchangeRate.ShouldBeNull("the rate is assigned on issue, not on draft creation");
        var done = await svc.CompleteInvoiceAsync(created.Id);

        done!.ExchangeRate.ShouldBe(24.465m);
        done.ExchangeRateDate.ShouldBe(new DateOnly(2026, 10, 2)); // Friday's fixing for a Saturday DUZP
    }

    [Fact]
    public async Task Complete_CzkInvoice_HasNoRateAndNeverAsksCnb()
    {
        var (svc, _, rates) = NewInvoiceService();

        var created = await svc.CreateInvoiceAsync(InvoiceDto(currencyId: 1));
        var done = await svc.CompleteInvoiceAsync(created.Id);

        done!.ExchangeRate.ShouldBeNull();
        await rates.DidNotReceive().GetRateAsync(Arg.Any<string>(), Arg.Any<DateOnly>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Complete_ManualRate_IsKept_AndHasNoCnbDate()
    {
        var (svc, _, rates) = NewInvoiceService();

        var created = await svc.CreateInvoiceAsync(InvoiceDto(currencyId: 2, manualRate: 25m));
        var done = await svc.CompleteInvoiceAsync(created.Id);

        done!.ExchangeRate.ShouldBe(25m);
        done.ExchangeRateDate.ShouldBeNull();
        await rates.DidNotReceive().GetRateAsync(Arg.Any<string>(), Arg.Any<DateOnly>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Complete_WhenCnbHasNoRate_StillIssues_WithoutRate()
    {
        var (svc, _, rates) = NewInvoiceService();
        rates.GetRateAsync(Arg.Any<string>(), Arg.Any<DateOnly>(), Arg.Any<CancellationToken>()).Returns((ExchangeRateDto?)null);

        var created = await svc.CreateInvoiceAsync(InvoiceDto(currencyId: 2));
        var done = await svc.CompleteInvoiceAsync(created.Id);

        done!.Status.ShouldBe(EInvoiceStatus.Completed);
        done.ExchangeRate.ShouldBeNull();
    }

    [Fact]
    public async Task Update_ManualRate_AllowedOnDraft_RejectedAfterCompletion()
    {
        var (svc, _, rates) = NewInvoiceService();
        rates.GetRateAsync(Arg.Any<string>(), Arg.Any<DateOnly>(), Arg.Any<CancellationToken>())
            .Returns(new ExchangeRateDto { CurrencyCode = "EUR", Amount = 1, Rate = 24m, RatePerUnit = 24m, ValidFor = new DateOnly(2026, 10, 2) });
        var created = await svc.CreateInvoiceAsync(InvoiceDto(currencyId: 2));

        var updated = await svc.UpdateInvoiceAsync(created.Id, new UpdateInvoiceDto { ExchangeRate = 26m });
        updated!.ExchangeRate.ShouldBe(26m);

        await svc.CompleteInvoiceAsync(created.Id);
        await Should.ThrowAsync<InvalidOperationException>(() => svc.UpdateInvoiceAsync(created.Id, new UpdateInvoiceDto { ExchangeRate = 27m }));
    }

    [Fact]
    public async Task CreditNote_InheritsRateAndDateOfOriginalInvoice()
    {
        var (svc, _, rates) = NewInvoiceService();
        rates.GetRateAsync(Arg.Any<string>(), Arg.Any<DateOnly>(), Arg.Any<CancellationToken>())
            .Returns(new ExchangeRateDto { CurrencyCode = "EUR", Amount = 1, Rate = 24.465m, RatePerUnit = 24.465m, ValidFor = new DateOnly(2026, 10, 2) });
        var original = await svc.CreateInvoiceAsync(InvoiceDto(currencyId: 2));
        await svc.CompleteInvoiceAsync(original.Id);

        // Even if the rate on the credit note's own DUZP were different, it must follow the original.
        rates.GetRateAsync(Arg.Any<string>(), Arg.Any<DateOnly>(), Arg.Any<CancellationToken>())
            .Returns(new ExchangeRateDto { CurrencyCode = "EUR", Amount = 1, Rate = 99m, RatePerUnit = 99m, ValidFor = new DateOnly(2026, 11, 2) });
        var cn = await svc.CreateCreditNoteAsync(original.Id, InvoiceDto(currencyId: 2, type: EDocumentType.CreditNote, original: original.Id));
        var done = await svc.CompleteInvoiceAsync(cn.Id);

        done!.ExchangeRate.ShouldBe(24.465m);
        done.ExchangeRateDate.ShouldBe(new DateOnly(2026, 10, 2));
    }

    // ── CZK conversion ───────────────────────────────────────────────────────

    [Fact]
    public async Task ConvertToCzk_UsesPerUnitRate_AndThrowsWhenNoRate()
    {
        var rates = Substitute.For<IExchangeRateService>();
        rates.GetRateAsync("HUF", Arg.Any<DateOnly>(), Arg.Any<CancellationToken>())
            .Returns(new ExchangeRateDto { CurrencyCode = "HUF", Amount = 100, Rate = 6.660m, RatePerUnit = 0.0666m, ValidFor = new DateOnly(2026, 10, 1) });
        var svc = new CurrencyService(null!, _master, Substitute.For<ITenantResolver>(),
            Substitute.For<ILogger<CurrencyService>>(), rates);

        (await svc.ConvertToCzkAsync(10000m, "HUF", new DateOnly(2026, 10, 1))).ShouldBe(666m);
        (await svc.ConvertToCzkAsync(100m, "CZK", new DateOnly(2026, 10, 1))).ShouldBe(100m);
        await Should.ThrowAsync<Fakvio.Application.Exceptions.ExchangeRateUnavailableException>(
            () => svc.ConvertToCzkAsync(100m, "USD", new DateOnly(2026, 10, 1))); // no rate: never the foreign amount as CZK
        (await svc.ConvertToCzkAsync(10m, "USD", new DateOnly(2026, 10, 1), storedRate: 25m)).ShouldBe(250m); // stored document rate wins
    }

    // ── PDF block ────────────────────────────────────────────────────────────

    private static Invoice PdfInvoice(bool vatPayer = true, decimal? rate = 24.465m, EDocumentType type = EDocumentType.Invoice)
    {
        var inv = new Invoice
        {
            DocumentType = type,
            Currency = new Currency { Code = "EUR", Symbol = "EUR" },
            Issuer = new Client { IsVatPayer = vatPayer },
            Client = new Client { Language = "cs" },
            ExchangeRate = rate,
            ExchangeRateDate = new DateOnly(2026, 10, 2),
        };
        inv.InvoiceItem.Add(new InvoiceItem { Description = "a", Quantity = 1, UnitPrice = 100, TotalBeforeVat = 100, VatRatePercentage = 21, VatAmount = 21, TotalWithVat = 121 });
        inv.InvoiceItem.Add(new InvoiceItem { Description = "b", Quantity = 1, UnitPrice = 50, TotalBeforeVat = 50, VatRatePercentage = 21, VatAmount = 10.5m, TotalWithVat = 60.5m, VatRegime = EVatRegime.ReverseCharge });
        return inv;
    }

    [Fact]
    public void Pdf_ForeignInvoiceOfVatPayer_PrintsRateAndCzkVatRecap()
    {
        var html = PdfExportService.BuildCzkRecapBlock(PdfInvoice(), "cs");

        html.ShouldContain("Kurz ČNB ke dni 02.10.2026: 1 EUR = 24,465 CZK");
        // 100 EUR x 24.465 = 2 446.50 base; VAT from the CZK base = 513.77 (21 %)
        html.ShouldContain("2");              // amounts are culture-formatted; check the VAT value digits
        html.ShouldContain("513");
        html.ShouldContain("daň odvede zákazník"); // reverse-charge line: base only
        html.ShouldContain("DPH celkem v CZK");
    }

    [Fact]
    public void Pdf_CzkVatTotal_IsComputedFromCzkBase()
    {
        var inv = PdfInvoice();
        inv.InvoiceItem.Remove(inv.InvoiceItem.Last()); // drop the reverse-charge item
        var html = PdfExportService.BuildCzkRecapBlock(inv, "en");

        html.ShouldContain("CNB exchange rate of 02.10.2026: 1 EUR = 24.465 CZK");
        html.ShouldContain($"Total VAT in CZK: {513.77m:N2} CZK"); // round(2446.50 x 21 %, 2)
    }

    [Theory]
    [InlineData(false, 24.465, EDocumentType.Invoice)]   // non-VAT-payer issuer
    [InlineData(true, null, EDocumentType.Invoice)]      // rate unknown
    [InlineData(true, 24.465, EDocumentType.Proforma)]   // not a tax document
    public void Pdf_BlockIsOmittedWhenNotApplicable(bool vatPayer, double? rate, EDocumentType type) =>
        PdfExportService.BuildCzkRecapBlock(PdfInvoice(vatPayer, (decimal?)rate, type), "cs").ShouldBeEmpty();

    [Fact]
    public void Pdf_BlockIsOmittedForCzkAndOss()
    {
        var czk = PdfInvoice(); czk.Currency = new Currency { Code = "CZK" };
        var oss = PdfInvoice(); oss.OssCountryCode = "DE";
        PdfExportService.BuildCzkRecapBlock(czk, "cs").ShouldBeEmpty();
        PdfExportService.BuildCzkRecapBlock(oss, "cs").ShouldBeEmpty();
    }

    [Fact]
    public void Pdf_ManualRate_HasNoCnbDateInRateLine()
    {
        var inv = PdfInvoice(); inv.ExchangeRateDate = null;
        PdfExportService.BuildCzkRecapBlock(inv, "cs").ShouldContain("Použitý kurz: 1 EUR = 24,465 CZK");
    }
}

using System.Net;
using Fakvio.Application.Service;
using Fakvio.Contracts.Dto.Invoice;
using Fakvio.Contracts.Dto.OssVatRate;
using Fakvio.Domain.Entities;
using Fakvio.Domain.Enums;
using Fakvio.Infrastructure.Data;
using Fakvio.Infrastructure.Service;
using Fakvio.Infrastructure.Service.Oss;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging;
using NSubstitute;
using Shouldly;

namespace Fakvio.Tests.Unit;

/// <summary>
/// EU OSS (One-Stop-Shop, DEVGUIDE §4.16): detection matrix, destination rate validation,
/// quarterly report aggregation + ECB conversion, exclusion from the CZ VAT report.
/// </summary>
public class OssTests : IDisposable
{
    private const long CompanyId = 77;

    private readonly TenantDbContext _tenant;
    private readonly MasterDbContext _master;
    private readonly IEcbExchangeRateClient _ecb = Substitute.For<IEcbExchangeRateClient>();
    private readonly InvoiceService _invoices;
    private int _seq; // unique document numbers -> unique variable symbols

    public OssTests()
    {
        _tenant = new TenantDbContext(new DbContextOptionsBuilder<TenantDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);
        _master = new MasterDbContext(new DbContextOptionsBuilder<MasterDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);

        var resolver = Substitute.For<ITenantResolver>();
        resolver.GetCurrentCompanyId().Returns(CompanyId);

        var numbers = Substitute.For<INumberSequenceService>();
        numbers.GenerateNextNumberForDocumentTypeAsync(
                Arg.Any<EDocumentType>(), Arg.Any<DateTime>(), Arg.Any<string?>(), Arg.Any<string?>(), Arg.Any<CancellationToken>())
            .Returns(_ => $"INV-2026-{++_seq:D3}");

        _invoices = new InvoiceService(_tenant, _master, resolver, numbers,
            Substitute.For<ITenantReadinessService>(), Substitute.For<ILogger<InvoiceService>>());

        Seed();
    }

    public void Dispose()
    {
        _tenant.Dispose();
        _master.Dispose();
    }

    // ── seed ─────────────────────────────────────────────────────────────────

    private void Seed()
    {
        _tenant.Currency.AddRange(
            new Currency { Id = 1, Code = "CZK", Name = "Koruna", Symbol = "Kc", DecimalPlaces = 2, SortOrder = 1, IsActive = true },
            new Currency { Id = 2, Code = "EUR", Name = "Euro", Symbol = "EUR", DecimalPlaces = 2, SortOrder = 2, IsActive = true });
        _tenant.VatRate.Add(new VatRate { Id = 1, Name = "21", Rate = 21, IsDefault = true, ValidFrom = DateTime.UtcNow.AddYears(-5), IsActive = true });

        // Issuer = VAT payer (id 1)
        _tenant.Client.Add(new Client { Id = 1, CompanyName = "Issuer", RegistrationNumber = "1", IsIssuer = true, IsActive = true, IsVatPayer = true });
        // B2C consumers: DE (10), FR by name (11), CZ (12), US (13); B2B with VAT id in DE (14)
        AddClient(10, "Hans", "DE");
        AddClient(11, "Pierre", "Francie");
        AddClient(12, "Jan", "CZ");
        AddClient(13, "Bob", "US");
        AddClient(14, "GmbH", "DE", taxNumber: "DE123456789", vatPayer: true);
        AddClient(15, "Mari", "Estonia");
        _tenant.SaveChanges();

        _master.CompanySystemSettings.Add(new CompanySystemSettings
        {
            CompanyId = CompanyId,
            SchemaName = "t77",
            OssRegistered = true,
            OssRegisteredSince = new DateTime(2025, 1, 1, 0, 0, 0, DateTimeKind.Utc)
        });
        var validFrom = new DateOnly(2021, 7, 1);
        _master.OssVatRate.AddRange(
            new OssVatRate { CountryCode = "DE", Rate = 19m, Category = EOssVatRateCategory.Standard, ValidFrom = validFrom, IsActive = true },
            new OssVatRate { CountryCode = "DE", Rate = 7m, Category = EOssVatRateCategory.Reduced, ValidFrom = validFrom, IsActive = true },
            new OssVatRate { CountryCode = "EE", Rate = 22m, Category = EOssVatRateCategory.Standard, ValidFrom = new DateOnly(2024, 1, 1), ValidTo = new DateOnly(2025, 6, 30), IsActive = true },
            new OssVatRate { CountryCode = "EE", Rate = 24m, Category = EOssVatRateCategory.Standard, ValidFrom = new DateOnly(2025, 7, 1), IsActive = true },
            new OssVatRate { CountryCode = "FR", Rate = 20m, Category = EOssVatRateCategory.Standard, ValidFrom = validFrom, IsActive = true });
        _master.SaveChanges();
    }

    private void AddClient(long id, string name, string country, string? taxNumber = null, bool vatPayer = false)
    {
        var c = new Client { Id = id, CompanyName = name, RegistrationNumber = id.ToString(), IsActive = true, TaxNumber = taxNumber, IsVatPayer = vatPayer };
        c.Address.Add(new Address { ClientId = id, Country = country, IsPrimary = true, Street = "s", City = "c", PostalCode = "1" });
        _tenant.Client.Add(c);
    }

    private static CreateInvoiceDto Dto(long clientId, decimal rate, long? vatRateId = null, long currencyId = 1,
        EDocumentType type = EDocumentType.Invoice, long? original = null, DateTime? duzp = null, bool? applyOss = null) => new()
    {
        // OSS is opt-in; by default the helper opts in for the two eligible consumer clients (10 = DE, 11 = FR).
        ApplyOss = applyOss ?? clientId is 10 or 11 or 15,
        DocumentType = type,
        ClientId = clientId,
        IssuerId = 1,
        CurrencyId = currencyId,
        OriginalInvoiceId = original,
        IssueDate = duzp ?? new DateTime(2026, 2, 10, 0, 0, 0, DateTimeKind.Utc),
        TaxableSupplyDate = duzp ?? new DateTime(2026, 2, 10, 0, 0, 0, DateTimeKind.Utc),
        InvoiceItem = [new() { OrderIndex = 1, Description = "x", Quantity = 1, Unit = "ks", UnitPrice = 100, VatRatePercentage = rate, VatRateId = vatRateId }]
    };

    // ── detection matrix (pure function) ─────────────────────────────────────

    private static Client Consumer(string country, string? tax = null, bool vat = false)
    {
        var c = new Client { TaxNumber = tax, IsVatPayer = vat };
        c.Address.Add(new Address { Country = country, IsPrimary = true });
        return c;
    }

    [Theory]
    [InlineData(true, true, "DE", null, false, EDocumentType.Invoice, "DE")]
    [InlineData(true, true, "Německo", null, false, EDocumentType.Invoice, "DE")]
    [InlineData(true, true, "France", null, false, EDocumentType.TaxReceiptForAdvance, "FR")]
    [InlineData(true, true, "EL", null, false, EDocumentType.Invoice, "GR")]
    [InlineData(false, true, "DE", null, false, EDocumentType.Invoice, null)]  // issuer not OSS registered
    [InlineData(true, false, "DE", null, false, EDocumentType.Invoice, null)]  // issuer not a VAT payer
    [InlineData(true, true, "CZ", null, false, EDocumentType.Invoice, null)]   // domestic
    [InlineData(true, true, "", null, false, EDocumentType.Invoice, null)]     // no country = CZ
    [InlineData(true, true, "US", null, false, EDocumentType.Invoice, null)]   // export, not OSS
    [InlineData(true, true, "Atlantis", null, false, EDocumentType.Invoice, null)] // unknown text is never guessed
    [InlineData(true, true, "DE", "DE123456789", false, EDocumentType.Invoice, null)] // B2B: has tax number
    [InlineData(true, true, "DE", null, true, EDocumentType.Invoice, null)]    // B2B: flagged VAT payer
    [InlineData(true, true, "DE", null, false, EDocumentType.Proforma, null)]  // proforma is no tax document
    [InlineData(true, true, "DE", null, false, EDocumentType.CreditNote, null)] // credit notes inherit, never re-detected
    public void OssDetector_Matrix(bool registered, bool issuerVat, string country, string? tax, bool clientVat,
        EDocumentType type, string? expected)
    {
        OssDetector.DetermineCountryCode(registered, issuerVat, Consumer(country, tax, clientVat), type).ShouldBe(expected);
    }

    [Fact]
    public void OssDetector_NoClient_IsNotOss()
        => OssDetector.DetermineCountryCode(true, true, null, EDocumentType.Invoice).ShouldBeNull();

    // ── InvoiceService: detection + rate validation ──────────────────────────

    [Fact]
    public async Task Create_ConsumerInGermany_IsOssWithDestinationRate()
    {
        var result = await _invoices.CreateInvoiceAsync(Dto(10, 19));

        result.OssCountryCode.ShouldBe("DE");
        result.InvoiceItem.Single().VatRatePercentage.ShouldBe(19m);
        result.TotalVat.ShouldBe(19m);
    }

    [Fact]
    public async Task Create_OssInvoice_DropsTenantVatRateId()
    {
        // A client (e.g. MCP) that still sends the CZ VatRateId must not break the invoice: the id is ignored.
        var result = await _invoices.CreateInvoiceAsync(Dto(10, 19, vatRateId: 1));

        result.InvoiceItem.Single().VatRateId.ShouldBeNull();
        result.InvoiceItem.Single().VatRatePercentage.ShouldBe(19m);
    }

    [Fact]
    public async Task Create_OssInvoice_RejectsCzechRate()
    {
        var ex = await Should.ThrowAsync<InvalidOperationException>(() => _invoices.CreateInvoiceAsync(Dto(10, 21)));
        ex.Message.ShouldContain("not a valid OSS rate for DE");
        ex.Message.ShouldContain("19");
    }

    [Fact]
    public async Task Create_OssInvoice_RejectsRateOutsideValidity()
    {
        // DUZP before the rate's ValidFrom -> no valid rate on that date.
        var ex = await Should.ThrowAsync<InvalidOperationException>(
            () => _invoices.CreateInvoiceAsync(Dto(10, 19, duzp: new DateTime(2020, 1, 1, 0, 0, 0, DateTimeKind.Utc))));
        ex.Message.ShouldContain("not a valid OSS rate");
    }

    [Fact]
    public async Task Create_OssInvoice_RejectsReverseChargeRegime()
    {
        var dto = Dto(10, 19);
        dto.InvoiceItem[0].VatRegime = EVatRegime.Exempt;
        var ex = await Should.ThrowAsync<InvalidOperationException>(() => _invoices.CreateInvoiceAsync(dto));
        ex.Message.ShouldContain("Standard VAT regime");
    }

    // ── opt-in (ApplyOss) ─────────────────────────────────────────────────────

    [Fact]
    public async Task Create_EligibleButNotOptedIn_IsOrdinaryCzInvoice()
    {
        // General B2C services are taxed in CZ: an eligible client alone must NOT switch the invoice to OSS.
        var result = await _invoices.CreateInvoiceAsync(Dto(10, 21, vatRateId: 1, applyOss: false));
        result.OssCountryCode.ShouldBeNull();
        result.InvoiceItem.Single().VatRatePercentage.ShouldBe(21m);
    }

    [Theory]
    [InlineData(12L)] // CZ consumer
    [InlineData(13L)] // US consumer
    [InlineData(14L)] // German business
    public async Task Create_ApplyOssOnIneligibleInvoice_IsRejected(long clientId)
    {
        var ex = await Should.ThrowAsync<InvalidOperationException>(() => _invoices.CreateInvoiceAsync(Dto(clientId, 21, vatRateId: 1, applyOss: true)));
        ex.Message.ShouldContain("OSS cannot be applied");
    }

    // ── update path: re-detection ─────────────────────────────────────────────

    private static UpdateInvoiceDto Upd(decimal rate, long? vatRateId = null, bool? applyOss = null) => new()
    {
        ApplyOss = applyOss,
        InvoiceItem = [new() { OrderIndex = 1, Description = "x", Quantity = 1, Unit = "ks", UnitPrice = 100, VatRatePercentage = rate, VatRateId = vatRateId }]
    };

    [Fact]
    public async Task Update_KeepsOssByDefault_AndValidatesRates()
    {
        var created = await _invoices.CreateInvoiceAsync(Dto(10, 19));

        var ok = await _invoices.UpdateInvoiceAsync(created.Id, Upd(7));
        ok!.OssCountryCode.ShouldBe("DE");
        ok.InvoiceItem.Single().VatRatePercentage.ShouldBe(7m);

        await Should.ThrowAsync<InvalidOperationException>(() => _invoices.UpdateInvoiceAsync(created.Id, Upd(21)));
    }

    [Fact]
    public async Task Update_OptOut_SwitchesToOrdinaryCzRates()
    {
        var created = await _invoices.CreateInvoiceAsync(Dto(10, 19));

        var result = await _invoices.UpdateInvoiceAsync(created.Id, Upd(21, vatRateId: 1, applyOss: false));

        result!.OssCountryCode.ShouldBeNull();
    }

    [Fact]
    public async Task Update_OptIn_OnOrdinaryInvoice_SwitchesToOss()
    {
        var created = await _invoices.CreateInvoiceAsync(Dto(10, 21, vatRateId: 1, applyOss: false));

        var result = await _invoices.UpdateInvoiceAsync(created.Id, Upd(19, applyOss: true));

        result!.OssCountryCode.ShouldBe("DE");
    }

    [Fact]
    public async Task Update_AfterEligibilityLost_Throws_UntilOptedOut()
    {
        var created = await _invoices.CreateInvoiceAsync(Dto(10, 19));
        var settings = await _master.CompanySystemSettings.SingleAsync();
        settings.OssRegistered = false; // registration ended meanwhile
        await _master.SaveChangesAsync();

        await Should.ThrowAsync<InvalidOperationException>(() => _invoices.UpdateInvoiceAsync(created.Id, Upd(19)));
        (await _invoices.UpdateInvoiceAsync(created.Id, Upd(21, vatRateId: 1, applyOss: false)))!.OssCountryCode.ShouldBeNull();
    }

    [Fact]
    public async Task Update_ClientAddressMovesToCz_Throws()
    {
        var created = await _invoices.CreateInvoiceAsync(Dto(10, 19));
        var address = await _tenant.Address.SingleAsync(a => a.ClientId == 10);
        address.Country = "CZ";
        await _tenant.SaveChangesAsync();

        await Should.ThrowAsync<InvalidOperationException>(() => _invoices.UpdateInvoiceAsync(created.Id, Upd(19)));
    }

    // ── rate change between invoice and credit note / later edit ───────────────

    private static readonly DateTime May2025 = new(2025, 5, 10, 0, 0, 0, DateTimeKind.Utc);
    private static readonly DateTime Aug2025 = new(2025, 8, 10, 0, 0, 0, DateTimeKind.Utc);

    [Fact]
    public async Task CreditNote_AfterRateChange_UsesOriginalInvoiceRate()
    {
        var original = await _invoices.CreateInvoiceAsync(Dto(15, 22, duzp: May2025)); // EE 22 % valid in May 2025

        // EE moved to 24 % on 2025-07-01; the credit note (August) corrects the May supply, so 22 % must be accepted.
        var credit = await _invoices.CreateInvoiceAsync(Dto(15, 22, type: EDocumentType.CreditNote, original: original.Id, duzp: Aug2025));

        credit.OssCountryCode.ShouldBe("EE");
        credit.InvoiceItem.Single().VatRatePercentage.ShouldBe(22m);
    }

    [Fact]
    public async Task NewInvoiceAfterRateChange_RejectsOldRate()
    {
        await Should.ThrowAsync<InvalidOperationException>(() => _invoices.CreateInvoiceAsync(Dto(15, 22, duzp: Aug2025)));
        (await _invoices.CreateInvoiceAsync(Dto(15, 24, duzp: Aug2025))).OssCountryCode.ShouldBe("EE");
    }

    [Fact]
    public async Task EditingOldInvoice_KeepsItsOwnPeriodRate()
    {
        var created = await _invoices.CreateInvoiceAsync(Dto(15, 22, duzp: May2025));

        var edited = await _invoices.UpdateInvoiceAsync(created.Id, Upd(22));

        edited!.InvoiceItem.Single().VatRatePercentage.ShouldBe(22m);
    }

    [Fact]
    public async Task Update_OptOut_WithoutVatRateId_AssignsTenantRate()
    {
        var created = await _invoices.CreateInvoiceAsync(Dto(10, 19));

        // The OSS items carry no VatRateId; switching back to CZ must not fail on "item without VAT rate".
        var result = await _invoices.UpdateInvoiceAsync(created.Id, Upd(21, applyOss: false));

        result!.OssCountryCode.ShouldBeNull();
        result.InvoiceItem.Single().VatRateId.ShouldBe(1);
    }

    [Fact]
    public async Task Create_DomesticConsumer_IsOrdinaryInvoice()
    {
        var result = await _invoices.CreateInvoiceAsync(Dto(12, 21, vatRateId: 1));
        result.OssCountryCode.ShouldBeNull();
    }

    [Fact]
    public async Task Create_BusinessInGermany_IsNotOss()
    {
        // B2B with a VAT id is reverse-charge territory, never OSS — the CZ rate path stays as before.
        var result = await _invoices.CreateInvoiceAsync(Dto(14, 21, vatRateId: 1));
        result.OssCountryCode.ShouldBeNull();
    }

    [Fact]
    public async Task Create_NotRegistered_IsNotOss()
    {
        var settings = await _master.CompanySystemSettings.SingleAsync();
        settings.OssRegistered = false;
        await _master.SaveChangesAsync();

        await Should.ThrowAsync<InvalidOperationException>(() => _invoices.CreateInvoiceAsync(Dto(10, 21, vatRateId: 1))); // opt-in refused
        var result = await _invoices.CreateInvoiceAsync(Dto(10, 21, vatRateId: 1, applyOss: false));
        result.OssCountryCode.ShouldBeNull();
    }

    [Fact]
    public async Task CreditNote_InheritsOssCountryFromOriginal()
    {
        var original = await _invoices.CreateInvoiceAsync(Dto(10, 19));

        var credit = await _invoices.CreateInvoiceAsync(Dto(10, 19, type: EDocumentType.CreditNote, original: original.Id));

        credit.OssCountryCode.ShouldBe("DE");
    }

    [Fact]
    public async Task GetOssCountry_PreviewMatchesDetection()
    {
        (await _invoices.GetOssCountryCodeAsync(11, 1, EDocumentType.Invoice)).ShouldBe("FR");
        (await _invoices.GetOssCountryCodeAsync(12, 1, EDocumentType.Invoice)).ShouldBeNull();
        (await _invoices.GetOssCountryCodeAsync(13, 1, EDocumentType.Invoice)).ShouldBeNull();
    }

    // ── PDF ──────────────────────────────────────────────────────────────────

    [Fact]
    public void Pdf_OssInvoice_ShowsDestinationRateLabelAndRegimeNote()
    {
        var invoice = new Invoice
        {
            OssCountryCode = "DE",
            Issuer = new Client { IsVatPayer = true },
            Client = new Client { Language = "cs" },
            Currency = new Currency { Symbol = "Kc" },
            InvoiceItem = [new InvoiceItem { OrderIndex = 1, Description = "x", Unit = "ks", Quantity = 1, UnitPrice = 100, TotalBeforeVat = 100, VatRatePercentage = 19m, VatAmount = 19m }]
        };

        var html = PdfExportService.ReplacePlaceholders(DefaultSeedData.GetDefaultInvoicePdfTemplate(), invoice);

        html.ShouldContain("DPH DE 19 %");
        html.ShouldContain("Režim OSS");
    }

    [Fact]
    public void Pdf_OrdinaryInvoice_HasNoOssMarkup()
    {
        var invoice = new Invoice
        {
            Issuer = new Client { IsVatPayer = true },
            Client = new Client { Language = "cs" },
            Currency = new Currency { Symbol = "Kc" },
            InvoiceItem = [new InvoiceItem { OrderIndex = 1, Description = "x", Unit = "ks", Quantity = 1, UnitPrice = 100, TotalBeforeVat = 100, VatRatePercentage = 21m, VatAmount = 21m }]
        };

        var html = PdfExportService.ReplacePlaceholders(DefaultSeedData.GetDefaultInvoicePdfTemplate(), invoice);

        html.ShouldNotContain("OSS");
    }

    // ── report: aggregation + ECB conversion ─────────────────────────────────

    private async Task<long> CompleteAsync(CreateInvoiceDto dto)
    {
        var created = await _invoices.CreateInvoiceAsync(dto);
        var entity = await _tenant.Invoice.FindAsync(created.Id);
        entity!.Status = EInvoiceStatus.Completed;
        await _tenant.SaveChangesAsync();
        return created.Id;
    }

    [Fact]
    public async Task Report_AggregatesPerCountryAndRate_ConvertsAtLastDayOfQuarter_CreditNotesNegative()
    {
        // Q1 2026 -> rate of 2026-03-31. 25 CZK per EUR keeps the maths exact.
        _ecb.GetUnitsPerEurAsync("CZK", new DateOnly(2026, 3, 31), Arg.Any<CancellationToken>()).Returns(25m);
        _ecb.GetUnitsPerEurAsync("EUR", Arg.Any<DateOnly>(), Arg.Any<CancellationToken>()).Returns(1m);

        // DE 19 %: 100 CZK invoice (VAT 19) + 100 EUR invoice (VAT 19)
        var original = await CompleteAsync(Dto(10, 19));
        await CompleteAsync(Dto(10, 19, currencyId: 2));
        // DE 7 %: one CZK invoice
        await CompleteAsync(Dto(10, 7));
        // FR 20 %: one CZK invoice, then a credit note for 50 % (typed as positive numbers — still reduces)
        var frOriginal = await CompleteAsync(Dto(11, 20));
        var credit = Dto(11, 20, type: EDocumentType.CreditNote, original: frOriginal);
        credit.InvoiceItem[0].UnitPrice = 50;
        await CompleteAsync(credit);
        // Outside the quarter and drafts must not count
        await CompleteAsync(Dto(10, 19, duzp: new DateTime(2026, 4, 2, 0, 0, 0, DateTimeKind.Utc)));
        await _invoices.CreateInvoiceAsync(Dto(10, 19)); // Draft
        _ = original;

        var report = await new OssReportService(_tenant, _ecb).GetReportAsync(2026, 1);

        report.DocumentCount.ShouldBe(5);
        var de19 = report.Lines.Single(l => l.CountryCode == "DE" && l.VatRate == 19m);
        de19.BaseEur.ShouldBe(104m);   // 100/25 + 100
        de19.VatEur.ShouldBe(19.76m);  // 19/25 + 19
        var de7 = report.Lines.Single(l => l.CountryCode == "DE" && l.VatRate == 7m);
        de7.BaseEur.ShouldBe(4m);
        de7.VatEur.ShouldBe(0.28m);
        var fr = report.Lines.Single(l => l.CountryCode == "FR");
        fr.BaseEur.ShouldBe(2m);       // (100 - 50) / 25
        fr.VatEur.ShouldBe(0.4m);      // (20 - 10) / 25
        report.TotalBaseEur.ShouldBe(110m);
        report.Lines.Select(l => l.CountryCode).ShouldBe(["DE", "DE", "FR"]); // ordered

        var csv = System.Text.Encoding.UTF8.GetString(new OssReportService(_tenant, _ecb).ToCsv(report)).TrimStart('﻿');
        csv.ShouldContain("DE;19;104.00;19.76");
        csv.ShouldContain("TOTAL;;110.00;");
    }

    [Fact]
    public async Task Report_EcbUnavailable_PropagatesClearError()
    {
        _ecb.GetUnitsPerEurAsync("CZK", Arg.Any<DateOnly>(), Arg.Any<CancellationToken>())
            .Returns<decimal>(_ => throw new EcbRateUnavailableException("down"));
        await CompleteAsync(Dto(10, 19));

        await Should.ThrowAsync<EcbRateUnavailableException>(() => new OssReportService(_tenant, _ecb).GetReportAsync(2026, 1));
    }

    [Fact]
    public async Task Report_NoOssDocuments_DoesNotCallEcb()
    {
        var report = await new OssReportService(_tenant, _ecb).GetReportAsync(2026, 1);

        report.Lines.ShouldBeEmpty();
        await _ecb.DidNotReceiveWithAnyArgs().GetUnitsPerEurAsync(default!, default);
    }

    [Theory]
    [InlineData(2026, 0)]
    [InlineData(2026, 5)]
    [InlineData(2019, 1)]
    public async Task Report_InvalidPeriod_Throws(int year, int quarter)
        => await Should.ThrowAsync<ArgumentOutOfRangeException>(() => new OssReportService(_tenant, _ecb).GetReportAsync(year, quarter));

    // ── exclusion from the CZ VAT report ─────────────────────────────────────

    [Fact]
    public async Task VatReport_ExcludesOssInvoices()
    {
        var czId = await CompleteAsync(Dto(12, 21, vatRateId: 1)); // domestic -> counted
        await CompleteAsync(Dto(10, 19));                          // OSS -> excluded
        _ = czId;

        var resolver = Substitute.For<ITenantResolver>();
        resolver.GetCurrentCompanyId().Returns(CompanyId);
        var vat = new VatReportService(_tenant, _master, resolver, Substitute.For<IEpoSchemaProvider>(),
            Substitute.For<ICurrencyService>(), Substitute.For<ILogger<VatReportService>>());

        var report = await vat.GetReportAsync(new DateTime(2026, 1, 1), new DateTime(2026, 3, 31));

        report.OutputVat.Select(v => v.VatRatePercentage).ShouldBe([21m]);
        report.TotalRevenue.ShouldBe(100m);
    }

    // ── rate service ─────────────────────────────────────────────────────────

    [Fact]
    public async Task RateService_ReturnsOnlyActiveRatesValidOnDate()
    {
        _master.OssVatRate.Add(new OssVatRate { CountryCode = "DE", Rate = 16m, Category = EOssVatRateCategory.Standard,
            ValidFrom = new DateOnly(2021, 7, 1), ValidTo = new DateOnly(2020, 12, 31), IsActive = true });
        _master.OssVatRate.Add(new OssVatRate { CountryCode = "DE", Rate = 5m, Category = EOssVatRateCategory.Reduced,
            ValidFrom = new DateOnly(2021, 7, 1), IsActive = false });
        await _master.SaveChangesAsync();
        var svc = new OssVatRateService(_master, Substitute.For<ILogger<OssVatRateService>>());

        var rates = await svc.GetForCountryAsync("de", new DateOnly(2026, 3, 1));

        rates.Select(r => r.Rate).ShouldBe([7m, 19m]);
    }

    [Fact]
    public async Task RateService_RejectsNonEuAndCzCountry()
    {
        var svc = new OssVatRateService(_master, Substitute.For<ILogger<OssVatRateService>>());
        foreach (var cc in new[] { "US", "CZ" })
            await Should.ThrowAsync<InvalidOperationException>(() => svc.CreateAsync(new CreateOssVatRateDto
            { CountryCode = cc, Rate = 10, ValidFrom = new DateOnly(2026, 1, 1) }));
    }
}

/// <summary>ECB client: CSV parsing, caching, error mapping (no real network).</summary>
public class EcbExchangeRateClientTests
{
    private const string Csv =
        "KEY,FREQ,CURRENCY,CURRENCY_DENOM,EXR_TYPE,EXR_SUFFIX,TIME_PERIOD,OBS_VALUE,OBS_STATUS\n" +
        "EXR.D.CZK.EUR.SP00.A,D,CZK,EUR,SP00,A,2026-03-31,25.20,A\n" +
        "EXR.D.CZK.EUR.SP00.A,D,CZK,EUR,SP00,A,2026-03-30,25.10,A\n" +
        "EXR.D.CZK.EUR.SP00.A,D,CZK,EUR,SP00,A,2026-04-01,25.30,A\n";

    private sealed class StubHandler(Func<HttpResponseMessage> respond) : HttpMessageHandler
    {
        public int Calls;
        public string? LastUrl;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Calls++;
            LastUrl = request.RequestUri!.ToString();
            return Task.FromResult(respond());
        }
    }

    private static EcbExchangeRateClient Client(StubHandler h) => new(new HttpClient(h), new MemoryCache(new MemoryCacheOptions()));

    [Fact]
    public void Parse_TakesEarliestObservationOnOrAfterDate_NeverEarlier()
    {
        // Saturday 2026-03-28: the next publication day is Monday 2026-03-30 (25.10), not an earlier day.
        EcbExchangeRateClient.ParseFirstObservationOnOrAfter(Csv, new DateOnly(2026, 3, 28)).ShouldBe(25.10m);
        // Exact day wins over later days.
        EcbExchangeRateClient.ParseFirstObservationOnOrAfter(Csv, new DateOnly(2026, 3, 31)).ShouldBe(25.20m);
        // Nothing published on/after the date yet.
        EcbExchangeRateClient.ParseFirstObservationOnOrAfter(Csv, new DateOnly(2026, 4, 2)).ShouldBeNull();
    }

    [Theory]
    [InlineData("")]
    [InlineData("KEY,TIME_PERIOD,OBS_VALUE\n")]
    [InlineData("garbage")]
    public void Parse_NoData_ReturnsNull(string csv)
        => EcbExchangeRateClient.ParseFirstObservationOnOrAfter(csv, new DateOnly(2026, 3, 31)).ShouldBeNull();

    [Fact]
    public async Task Eur_IsOneWithoutHttp()
    {
        var h = new StubHandler(() => new HttpResponseMessage(HttpStatusCode.OK));
        (await Client(h).GetUnitsPerEurAsync("eur", new DateOnly(2026, 3, 31))).ShouldBe(1m);
        h.Calls.ShouldBe(0);
    }

    [Fact]
    public async Task Rate_IsCached_AndAsksForLookbackWindow()
    {
        var h = new StubHandler(() => new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(Csv) });
        var client = Client(h);

        (await client.GetUnitsPerEurAsync("CZK", new DateOnly(2026, 3, 31))).ShouldBe(25.20m);
        (await client.GetUnitsPerEurAsync("CZK", new DateOnly(2026, 3, 31))).ShouldBe(25.20m);

        h.Calls.ShouldBe(1);
        h.LastUrl.ShouldContain("D.CZK.EUR.SP00.A");
        // looks FORWARD from the period end (next publication day), never back
        h.LastUrl.ShouldContain("startPeriod=2026-03-31&endPeriod=2026-04-10");
    }

    [Fact]
    public async Task WeekendPeriodEnd_UsesNextPublicationDay_AndNotYetPublishedIsNotCached()
    {
        var published = false;
        var h = new StubHandler(() => new HttpResponseMessage(HttpStatusCode.OK)
        {
            // Period ends Saturday 2026-03-28. The earlier Friday row must be ignored; Monday exists only once published.
            Content = new StringContent(published
                ? "KEY,TIME_PERIOD,OBS_VALUE\nX,2026-03-27,24.90\nX,2026-03-30,25.10\n"
                : "KEY,TIME_PERIOD,OBS_VALUE\nX,2026-03-27,24.90\n")
        });
        var client = Client(h);
        var date = new DateOnly(2026, 3, 28);

        await Should.ThrowAsync<EcbRateUnavailableException>(() => client.GetUnitsPerEurAsync("CZK", date)); // no fallback to Friday

        published = true;
        (await client.GetUnitsPerEurAsync("CZK", date)).ShouldBe(25.10m); // the miss was not cached
        h.Calls.ShouldBe(2);
    }

    [Fact]
    public async Task HttpError_MapsToRateUnavailable()
    {
        var client = Client(new StubHandler(() => new HttpResponseMessage(HttpStatusCode.ServiceUnavailable)));
        await Should.ThrowAsync<EcbRateUnavailableException>(() => client.GetUnitsPerEurAsync("CZK", new DateOnly(2026, 3, 31)));
    }

    [Fact]
    public async Task EmptyBody_MapsToRateUnavailable()
    {
        var client = Client(new StubHandler(() => new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("") }));
        await Should.ThrowAsync<EcbRateUnavailableException>(() => client.GetUnitsPerEurAsync("CZK", new DateOnly(2026, 3, 31)));
    }

    [Theory]
    [InlineData("CZK/../x")]
    [InlineData("12")]
    [InlineData("")]
    public async Task InvalidCurrencyCode_IsRejectedBeforeRequest(string code)
    {
        var h = new StubHandler(() => new HttpResponseMessage(HttpStatusCode.OK));
        await Should.ThrowAsync<EcbRateUnavailableException>(() => Client(h).GetUnitsPerEurAsync(code, new DateOnly(2026, 3, 31)));
        h.Calls.ShouldBe(0);
    }
}

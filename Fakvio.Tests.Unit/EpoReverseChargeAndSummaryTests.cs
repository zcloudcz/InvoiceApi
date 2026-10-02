using System.Text;
using System.Xml.Linq;
using Fakvio.Application.Service;
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
/// Reverse charge (PDP) in DPHDP3 / DPHKH1 and the EU summary statement (DPHSHV).
/// Every generated XML is XSD-validated by the service itself (it throws on a schema error),
/// so a successful call already proves the XML is schema-valid.
/// </summary>
public class EpoReverseChargeAndSummaryTests : IDisposable
{
    private const long IssuerId = 2;
    private const long CzCustomerId = 1;
    private const long DeCustomerId = 3;
    private const long FrCustomerId = 4;
    private const long RcCodeId = 7;

    private readonly TenantDbContext _context;
    private readonly MasterDbContext _masterContext;
    private readonly VatReportService _service;

    public EpoReverseChargeAndSummaryTests()
    {
        _context = new TenantDbContext(new DbContextOptionsBuilder<TenantDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);
        _masterContext = new MasterDbContext(new DbContextOptionsBuilder<MasterDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);

        var currency = Substitute.For<ICurrencyService>();
        currency.ConvertToCzkAsync(Arg.Any<decimal>(), Arg.Any<string>(), Arg.Any<DateOnly>(), Arg.Any<CancellationToken>())
            .Returns(call => Task.FromResult(call.ArgAt<decimal>(0)));

        var tenant = Substitute.For<ITenantResolver>();
        tenant.GetCurrentCompanyId().Returns((long?)IssuerId);

        _service = new VatReportService(_context, _masterContext, tenant,
            new EpoSchemaProvider(AppContext.BaseDirectory), currency, Substitute.For<ILogger<VatReportService>>());

        _context.Currency.Add(new Currency { Id = 1, Code = "CZK", Name = "Koruna", Symbol = "Kc", DecimalPlaces = 2, SortOrder = 1, IsActive = true });
        _context.ReverseChargeCode.Add(new ReverseChargeCode { Id = RcCodeId, Code = "4", NameCs = "Stavební práce", ParagraphRef = "§92e" });
        _context.Client.AddRange(
            Client(CzCustomerId, "CZ11111111"),
            Client(IssuerId, "CZ12345678", issuer: true),
            Client(DeCustomerId, "DE 123.456.789"),
            Client(FrCustomerId, "FR12345678901"));
        _context.SaveChanges();

        _masterContext.CompanySystemSettings.Add(new CompanySystemSettings
        {
            Id = 1, CompanyId = IssuerId, SchemaName = "tenant_2", IsProvisioned = true, IsActive = true,
            EpoTaxOfficeCode = 451, EpoTaxOfficeBranchCode = 2017
        });
        _masterContext.SaveChanges();
    }

    public void Dispose()
    {
        _context.Database.EnsureDeleted(); _context.Dispose();
        _masterContext.Database.EnsureDeleted(); _masterContext.Dispose();
    }

    private static Client Client(long id, string taxNumber, bool issuer = false) => new()
    {
        Id = id, CompanyName = "Firma " + id, RegistrationNumber = id.ToString("D8"), TaxNumber = taxNumber,
        IsIssuer = issuer, IsActive = true, IsVatPayer = true
    };

    private static readonly DateTime Duzp = new(2026, 3, 10, 0, 0, 0, DateTimeKind.Utc);

    /// <summary>Issued invoice with one item; regime/code default to a plain standard item.</summary>
    private void Issued(string number, decimal baseAmount, long clientId = CzCustomerId,
        EVatRegime regime = EVatRegime.Standard, decimal vatPct = 21m, string? ossCountry = null,
        EDocumentType docType = EDocumentType.Invoice)
    {
        var vat = regime == EVatRegime.Standard ? Math.Round(baseAmount * vatPct / 100m, 2) : 0m;
        _context.Invoice.Add(new Invoice
        {
            DocumentType = docType, Status = EInvoiceStatus.Completed, DocumentNumber = number, OssCountryCode = ossCountry,
            IssueDate = Duzp, TaxableSupplyDate = Duzp, DueDate = Duzp.AddDays(14),
            ClientId = clientId, IssuerId = IssuerId, Issuer = _context.Client.Find(IssuerId)!, CurrencyId = 1,
            TotalBeforeVat = baseAmount, TotalVat = vat, TotalWithVat = baseAmount + vat,
            InvoiceItem =
            {
                new InvoiceItem
                {
                    OrderIndex = 1, Description = "Item", Quantity = 1, UnitPrice = baseAmount,
                    VatRatePercentage = vatPct, TotalBeforeVat = baseAmount, VatAmount = vat, TotalWithVat = baseAmount + vat,
                    VatRegime = regime,
                    ReverseChargeCodeId = regime == EVatRegime.ReverseCharge ? RcCodeId : null
                }
            }
        });
        _context.SaveChanges();
    }

    /// <summary>Issued credit note (stored with the sign the user typed) to the given client.</summary>
    private void CreditNote(string number, decimal baseAmount, long clientId, DateTime? duzp = null)
    {
        var d = duzp ?? Duzp;
        _context.Invoice.Add(new Invoice
        {
            DocumentType = EDocumentType.CreditNote, Status = EInvoiceStatus.Completed, DocumentNumber = number,
            IssueDate = d, TaxableSupplyDate = d, DueDate = d.AddDays(14),
            ClientId = clientId, IssuerId = IssuerId, Issuer = _context.Client.Find(IssuerId)!, CurrencyId = 1,
            TotalBeforeVat = baseAmount, TotalVat = 0, TotalWithVat = baseAmount,
            InvoiceItem =
            {
                new InvoiceItem
                {
                    OrderIndex = 1, Description = "Credit", Quantity = 1, UnitPrice = baseAmount,
                    VatRatePercentage = 0, TotalBeforeVat = baseAmount, VatAmount = 0, TotalWithVat = baseAmount,
                    VatRegime = EVatRegime.OutOfScope
                }
            }
        });
        _context.SaveChanges();
    }

    private void Received(string number, decimal baseAmount, EVatRegime regime, decimal vatPct = 21m)
    {
        var rc = regime == EVatRegime.ReverseCharge;
        var vat = regime == EVatRegime.Standard ? Math.Round(baseAmount * vatPct / 100m, 2) : 0m;
        _context.ReceivedInvoice.Add(new ReceivedInvoice
        {
            DocumentNumber = number, Status = EReceivedInvoiceStatus.Approved, SupplierId = CzCustomerId,
            IssueDate = Duzp, ReceivedDate = Duzp, TaxableSupplyDate = Duzp, DueDate = Duzp.AddDays(30), CurrencyId = 1,
            TotalBeforeVat = baseAmount, TotalVat = vat, TotalWithVat = baseAmount + vat,
            Items =
            {
                new ReceivedInvoiceItem
                {
                    OrderIndex = 1, Description = "Item", Quantity = 1, UnitPrice = baseAmount,
                    VatRatePercentage = vatPct, TotalBeforeVat = baseAmount, VatAmount = vat, TotalWithVat = baseAmount + vat,
                    VatRegime = regime,
                    ReverseChargeCodeId = rc ? RcCodeId : null,
                    InformationalVatAmount = rc ? Math.Round(baseAmount * vatPct / 100m, 2) : 0m
                }
            }
        });
        _context.SaveChanges();
    }

    private static XDocument Parse(byte[] bytes) => XDocument.Parse(Encoding.UTF8.GetString(bytes));

    private async Task<XDocument> Return() =>
        Parse(await _service.ExportEpoVatReturnAsync(2026, 3, EVatPeriodType.Monthly));

    private async Task<XDocument> Control() =>
        Parse(await _service.ExportEpoControlStatementAsync(2026, 3, EVatPeriodType.Monthly));

    // ── DPHDP3 ────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Dphdp3_IssuedReverseCharge_GoesToRow25_NotToStandardRows()
    {
        Issued("INV-RC", 50_000m, regime: EVatRegime.ReverseCharge);
        Issued("INV-STD", 10_000m);

        var doc = await Return();

        doc.Descendants("Veta2").Single().Attribute("pln_rez_pren")!.Value.ShouldBe("50000");
        var veta1 = doc.Descendants("Veta1").Single();
        veta1.Attribute("obrat23")!.Value.ShouldBe("10000", "RC base must not leak into row 1");
        veta1.Attribute("rez_pren23").ShouldBeNull();
    }

    [Fact]
    public async Task Dphdp3_ReceivedReverseCharge_ReportsOutputAndDeduction_NetZero()
    {
        Received("REC-RC", 20_000m, EVatRegime.ReverseCharge);

        var doc = await Return();

        var veta1 = doc.Descendants("Veta1").Single();
        veta1.Attribute("rez_pren23")!.Value.ShouldBe("20000");
        veta1.Attribute("dan_rpren23")!.Value.ShouldBe("4200");

        var veta4 = doc.Descendants("Veta4").Single();
        veta4.Attribute("od_zdp23")!.Value.ShouldBe("20000");
        veta4.Attribute("nar_zdp23")!.Value.ShouldBe("4200");
        veta4.Attribute("odp_sum_nar")!.Value.ShouldBe("4200", "row 51 includes the self-assessed deduction");
    }

    [Fact]
    public async Task Dphdp3_ReceivedReverseCharge_ReducedRate_UsesRows11And44()
    {
        Received("REC-RC12", 10_000m, EVatRegime.ReverseCharge, vatPct: 12m);

        var doc = await Return();

        doc.Descendants("Veta1").Single().Attribute("dan_rpren5")!.Value.ShouldBe("1200");
        doc.Descendants("Veta4").Single().Attribute("nar_zdp5")!.Value.ShouldBe("1200");
    }

    [Fact]
    public async Task Dphdp3_MixedStandardAndReverseCharge_KeepsSidesSeparate()
    {
        Received("REC-STD", 10_000m, EVatRegime.Standard);
        Received("REC-RC", 5_000m, EVatRegime.ReverseCharge);
        Issued("INV-RC", 7_000m, regime: EVatRegime.ReverseCharge);

        var doc = await Return();

        var veta4 = doc.Descendants("Veta4").Single();
        veta4.Attribute("odp_tuz23_nar")!.Value.ShouldBe("2100"); // billed VAT only
        veta4.Attribute("nar_zdp23")!.Value.ShouldBe("1050");     // self-assessed
        veta4.Attribute("odp_sum_nar")!.Value.ShouldBe("3150");
        doc.Descendants("Veta2").Single().Attribute("pln_rez_pren")!.Value.ShouldBe("7000");
    }

    // ── DPHKH1 ────────────────────────────────────────────────────────────────

    [Fact]
    public async Task ReverseChargeCreditNote_IsNegativeInRow25AndA1UnderItsOwnNumber()
    {
        Issued("INV-RC", 50_000m, regime: EVatRegime.ReverseCharge);
        Issued("CN-RC", 20_000m, regime: EVatRegime.ReverseCharge, docType: EDocumentType.CreditNote); // stored positive

        (await Return()).Descendants("Veta2").Single().Attribute("pln_rez_pren")!.Value.ShouldBe("30000");

        var a1 = (await Control()).Descendants("VetaA1").ToList();
        a1.Count.ShouldBe(2);
        a1.Single(r => r.Attribute("c_evid_dd")!.Value == "CN-RC").Attribute("zakl_dane1")!.Value.ShouldBe("-20000.00");
        a1.Single(r => r.Attribute("c_evid_dd")!.Value == "INV-RC").Attribute("zakl_dane1")!.Value.ShouldBe("50000.00");
    }

    [Fact]
    public async Task Dphkh1_IssuedReverseCharge_ProducesA1RowWithCode_AndAggregatesSameDocCode()
    {
        Issued("INV-RC", 50_000m, regime: EVatRegime.ReverseCharge);

        var a1 = (await Control()).Descendants("VetaA1").Single();

        a1.Attribute("dic_odb")!.Value.ShouldBe("11111111");
        a1.Attribute("c_evid_dd")!.Value.ShouldBe("INV-RC");
        a1.Attribute("duzp")!.Value.ShouldBe("10.3.2026");
        a1.Attribute("zakl_dane1")!.Value.ShouldBe("50000.00");
        a1.Attribute("kod_pred_pl")!.Value.ShouldBe("4");
    }

    [Fact]
    public async Task Dphkh1_ReceivedReverseCharge_ProducesB1RowWithSelfAssessedTax()
    {
        Received("REC-RC", 20_000m, EVatRegime.ReverseCharge);

        var doc = await Control();
        var b1 = doc.Descendants("VetaB1").Single();

        b1.Attribute("dic_dod")!.Value.ShouldBe("11111111");
        b1.Attribute("c_evid_dd")!.Value.ShouldBe("REC-RC");
        b1.Attribute("zakl_dane1")!.Value.ShouldBe("20000.00");
        b1.Attribute("dan1")!.Value.ShouldBe("4200.00");
        b1.Attribute("kod_pred_pl")!.Value.ShouldBe("4");
        doc.Descendants("VetaB2").ShouldBeEmpty("RC must not also appear as an ordinary B.2/B.3 row");
        doc.Descendants("VetaB3").ShouldBeEmpty();
    }

    // ── DPHSHV ────────────────────────────────────────────────────────────────

    [Theory]
    [InlineData("DE 123.456.789", "DE", "123456789")]
    [InlineData("el123456789", "EL", "123456789")]
    [InlineData("CZ12345678", null, null)]   // domestic
    [InlineData("US123456789", null, null)]  // non-EU
    [InlineData("12345678", null, null)]     // no prefix
    [InlineData(null, null, null)]
    public void TryGetEuVatId_DetectsEuMemberStatesOtherThanCz(string? input, string? country, string? id)
    {
        var result = VatReportService.TryGetEuVatId(input);
        if (country is null) result.ShouldBeNull();
        else result.ShouldBe((country, id!));
    }

    [Fact]
    public async Task SummaryStatement_AggregatesByCustomer_DefaultsToServices_GoodsOverrideSwitchesCode()
    {
        Issued("INV-DE-1", 1_000.20m, DeCustomerId, EVatRegime.OutOfScope);
        Issued("INV-DE-2", 2_000m, DeCustomerId, EVatRegime.OutOfScope);
        Issued("INV-FR", 500m, FrCustomerId, EVatRegime.OutOfScope);
        Issued("INV-CZ", 9_999m); // domestic — excluded

        var rows = await _service.GetSummaryStatementRowsAsync(2026, 3, EVatPeriodType.Monthly);

        rows.Count.ShouldBe(2);
        var de = rows.Single(r => r.CountryCode == "DE");
        de.VatId.ShouldBe("123456789");
        de.InvoiceCount.ShouldBe(2);
        de.TotalCzk.ShouldBe(3_001, "3000.20 rounded UP to whole crowns");
        de.SupplyCode.ShouldBe(3);

        var goods = await _service.GetSummaryStatementRowsAsync(2026, 3, EVatPeriodType.Monthly, ["de123456789"]);
        goods.Single(r => r.CountryCode == "DE").SupplyCode.ShouldBe(0);
        goods.Single(r => r.CountryCode == "FR").SupplyCode.ShouldBe(3);
    }

    [Fact]
    public async Task SummaryStatement_StandardCzVatItemToEuClient_IsExcluded()
    {
        Issued("INV-DE-STD", 5_000m, DeCustomerId, EVatRegime.Standard); // billed CZ VAT -> domestic supply
        Issued("INV-DE-RC", 5_000m, DeCustomerId, EVatRegime.ReverseCharge);

        (await _service.GetSummaryStatementRowsAsync(2026, 3, EVatPeriodType.Monthly)).ShouldBeEmpty();
    }

    [Fact]
    public async Task SummaryStatement_MixedInvoice_CountsOnlyCustomerPaysItems()
    {
        var net = 1_000m;
        _context.Invoice.Add(new Invoice
        {
            DocumentType = EDocumentType.Invoice, Status = EInvoiceStatus.Completed, DocumentNumber = "INV-MIX",
            IssueDate = Duzp, TaxableSupplyDate = Duzp, DueDate = Duzp.AddDays(14), ClientId = DeCustomerId,
            IssuerId = IssuerId, Issuer = _context.Client.Find(IssuerId)!, CurrencyId = 1,
            TotalBeforeVat = 3 * net, TotalVat = 210, TotalWithVat = 3 * net + 210,
            InvoiceItem =
            {
                new InvoiceItem { OrderIndex = 1, Description = "a", Quantity = 1, UnitPrice = net, TotalBeforeVat = net, TotalWithVat = net, VatRegime = EVatRegime.OutOfScope },
                new InvoiceItem { OrderIndex = 2, Description = "b", Quantity = 1, UnitPrice = net, VatRatePercentage = 21, TotalBeforeVat = net, VatAmount = 210, TotalWithVat = net + 210, VatRegime = EVatRegime.Standard },
                new InvoiceItem { OrderIndex = 3, Description = "c", Quantity = 1, UnitPrice = net, TotalBeforeVat = net, TotalWithVat = net, VatRegime = EVatRegime.Exempt }
            }
        });
        _context.SaveChanges();

        var row = (await _service.GetSummaryStatementRowsAsync(2026, 3, EVatPeriodType.Monthly)).ShouldHaveSingleItem();
        row.TotalCzk.ShouldBe(2_000);
    }

    [Theory]
    [InlineData(100)]   // user stored a positive credit note
    [InlineData(-100)]  // user stored a negative credit note
    public async Task SummaryStatement_CreditNote_ReducesTotalAndCountsInPeriodOfItsOwnDuzp(decimal stored)
    {
        Issued("INV-DE", 1_000m, DeCustomerId, EVatRegime.OutOfScope);
        CreditNote("CN-DE", stored, DeCustomerId);
        CreditNote("CN-DE-APRIL", 5_000m, DeCustomerId, new DateTime(2026, 4, 2, 0, 0, 0, DateTimeKind.Utc)); // other period

        var row = (await _service.GetSummaryStatementRowsAsync(2026, 3, EVatPeriodType.Monthly)).ShouldHaveSingleItem();

        row.TotalCzk.ShouldBe(900);
        row.InvoiceCount.ShouldBe(2);
    }

    [Fact]
    public async Task SummaryStatement_QuarterlyWithGoods_Throws_ButQuarterlyServicesWorks()
    {
        Issued("INV-DE", 2_000m, DeCustomerId, EVatRegime.OutOfScope);

        var ex = await Should.ThrowAsync<InvalidOperationException>(() =>
            _service.ExportEpoSummaryStatementAsync(2026, 1, EVatPeriodType.Quarterly, ["DE123456789"]));
        ex.Message.ShouldContain("monthly");

        var doc = Parse(await _service.ExportEpoSummaryStatementAsync(2026, 1, EVatPeriodType.Quarterly));
        doc.Descendants("VetaD").Single().Attribute("ctvrt")!.Value.ShouldBe("1");
    }

    [Fact]
    public async Task Dphdp3_EuSupplies_FillRows20And21_MatchingSummaryStatement()
    {
        Issued("INV-DE", 3_000m, DeCustomerId, EVatRegime.OutOfScope);   // goods (flagged)
        Issued("INV-FR", 700m, FrCustomerId, EVatRegime.OutOfScope);     // services (default)
        var goods = new[] { "DE123456789" };

        var doc = Parse(await _service.ExportEpoVatReturnAsync(2026, 3, EVatPeriodType.Monthly, default, goods));

        var veta2 = doc.Descendants("Veta2").Single();
        veta2.Attribute("dod_zb")!.Value.ShouldBe("3000");
        veta2.Attribute("pln_sluzby")!.Value.ShouldBe("700");
    }

    [Fact]
    public async Task Dphkh1_ReverseCharge_WithoutCzDic_ThrowsWithDocumentNumber()
    {
        Received("REC-NODIC", 1_000m, EVatRegime.ReverseCharge);
        var supplier = _context.Client.Find(CzCustomerId)!;
        supplier.TaxNumber = null;
        _context.SaveChanges();

        var ex = await Should.ThrowAsync<InvalidOperationException>(() =>
            _service.ExportEpoControlStatementAsync(2026, 3, EVatPeriodType.Monthly));
        ex.Message.ShouldContain("REC-NODIC");
    }

    [Theory]
    [InlineData("GR123456789", null, "EL", "123456789")]
    [InlineData("123456789", "Německo", "DE", "123456789")]    // no prefix -> address country
    [InlineData("123456789", "Czech Republic", null, null)]    // domestic address
    [InlineData("123456789", null, null, null)]                // nothing to go on
    [InlineData("US123456789", "Německo", null, null)]         // explicit non-EU prefix wins
    public void TryGetEuVatId_FallbackToAddressCountry_AndGreeceMapping(string tax, string? addr, string? country, string? id)
    {
        var result = VatReportService.TryGetEuVatId(tax, addr);
        if (country is null) result.ShouldBeNull();
        else result.ShouldBe((country, id!));
    }

    [Fact]
    public async Task Dphkh1_B1Row_OmitsNothingWhenSupplierHasCzDic()
    {
        Received("REC-OK", 1_000m, EVatRegime.ReverseCharge);
        (await Control()).Descendants("VetaB1").ShouldHaveSingleItem();
    }

    [Fact]
    public async Task SummaryStatement_Xml_IsSchemaValidAndCarriesRows()
    {
        Issued("INV-DE-1", 3_000m, DeCustomerId, EVatRegime.OutOfScope);

        // The service validates against dphshv_epo2.xsd and throws on any schema error.
        var doc = Parse(await _service.ExportEpoSummaryStatementAsync(2026, 3, EVatPeriodType.Monthly, ["DE123456789"]));

        var d = doc.Descendants("VetaD").Single();
        d.Attribute("dokument")!.Value.ShouldBe("SHV");
        d.Attribute("shvies_forma")!.Value.ShouldBe("R");
        d.Attribute("mesic")!.Value.ShouldBe("3");

        var r = doc.Descendants("VetaR").Single();
        r.Attribute("k_stat")!.Value.ShouldBe("DE");
        r.Attribute("c_vat")!.Value.ShouldBe("123456789");
        r.Attribute("k_pln_eu")!.Value.ShouldBe("0");
        r.Attribute("pln_pocet")!.Value.ShouldBe("1");
        r.Attribute("pln_hodnota")!.Value.ShouldBe("3000");
    }

    [Fact]
    public async Task SummaryStatement_NothingToReport_Throws()
    {
        Issued("INV-CZ", 1_000m);

        await Should.ThrowAsync<InvalidOperationException>(
            () => _service.ExportEpoSummaryStatementAsync(2026, 3, EVatPeriodType.Monthly));
    }

    [Fact]
    public async Task Dphdp3_ReceivedReverseCharge_FromNonCzSupplier_ThrowsWithDocumentNumber()
    {
        Received("REC-DE", 1_000m, EVatRegime.ReverseCharge);
        _context.Client.Find(CzCustomerId)!.TaxNumber = "DE123456789";
        _context.SaveChanges();

        var ex = await Should.ThrowAsync<InvalidOperationException>(() =>
            _service.ExportEpoVatReturnAsync(2026, 3, EVatPeriodType.Monthly));
        ex.Message.ShouldContain("REC-DE");
    }

    [Fact]
    public async Task SummaryStatement_OssInvoice_IsExcluded()
    {
        Issued("INV-OSS", 2_000m, DeCustomerId, EVatRegime.OutOfScope, ossCountry: "DE");
        Issued("INV-B2B", 1_000m, DeCustomerId, EVatRegime.OutOfScope);

        var row = (await _service.GetSummaryStatementRowsAsync(2026, 3, EVatPeriodType.Monthly)).ShouldHaveSingleItem();
        row.TotalCzk.ShouldBe(1_000);
    }
}

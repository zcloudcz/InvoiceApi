using System.Xml;
using System.Xml.Linq;
using System.Xml.Schema;
using Fakvio.Application.Service;
using Fakvio.Domain.Entities;
using Fakvio.Domain.Enums;
using Fakvio.Infrastructure.Service;
using Fakvio.Infrastructure.Service.AccountingExport;
using Microsoft.Extensions.Logging;
using NSubstitute;
using Shouldly;

namespace Fakvio.Tests.Unit;

/// <summary>
/// Tests for the three accounting exporters. Output is validated against the vendors' own
/// definitions instead of echoing the exporter's strings back:
///  - POHODA: official Stormware XSDs (Schemas/Pohoda), version 2.
///  - Money S3: official Seyfor XML-transfer XSDs (Schemas/MoneyS3).
///  - ABRA Flexi: no XSD exists, so every element is checked against a snapshot of the Flexi
///    evidence catalogue (Schemas/AbraFlexi/catalog.txt: writable fields, types, lengths, select values).
/// See Schemas/README.md for provenance.
/// </summary>
public class AccountingExporterTests
{
    private static readonly XNamespace Inv = "http://www.stormware.cz/schema/version_2/invoice.xsd";
    private static readonly XNamespace Typ = "http://www.stormware.cz/schema/version_2/type.xsd";

    private static readonly Lazy<XmlSchemaSet> PohodaSchemas = new(() => LoadSchemas("Pohoda", "data.xsd"));
    private static readonly Lazy<XmlSchemaSet> MoneySchemas = new(() => LoadSchemas("MoneyS3", "_Document.xsd"));

    private static XmlSchemaSet LoadSchemas(string folder, string rootFile)
    {
        var set = new XmlSchemaSet { XmlResolver = new XmlUrlResolver() };
        set.Add(null, Path.Combine(AppContext.BaseDirectory, "Schemas", folder, rootFile));
        set.Compile();
        return set;
    }

    private static List<string> Validate(byte[] xml, XmlSchemaSet schemas)
    {
        var errors = new List<string>();
        var settings = new XmlReaderSettings { ValidationType = ValidationType.Schema, Schemas = schemas };
        settings.ValidationEventHandler += (_, e) => errors.Add($"{e.Severity}: {e.Message}");
        using var reader = XmlReader.Create(new MemoryStream(xml), settings);
        while (reader.Read()) { }
        return errors;
    }

    private static XDocument Parse(byte[] bytes) => XDocument.Load(new MemoryStream(bytes));

    // ── POHODA ────────────────────────────────────────────────────────────────

    [Fact]
    public void Pohoda_IssuedAndReceived_ValidateAgainstOfficialXsd()
    {
        var bytes = new PohodaAccountingExporter().Export(
            [Issued("INV1", ("A", 21m, 100m), ("B", 12m, 50m), ("C", 0m, 10m))],
            [Received("R1", "CZK", ("Hosting", 21m, 100m))], Issuer());

        string.Join(Environment.NewLine, Validate(bytes, PohodaSchemas.Value)).ShouldBeEmpty();
    }

    [Fact]
    public void Pohoda_IssuedInvoice_VatBreakdownMatchesItems()
    {
        var doc = Parse(new PohodaAccountingExporter().Export([Issued("INV1", ("A", 21m, 100m), ("B", 12m, 50m), ("C", 0m, 10m))], [], Issuer()));

        var home = doc.Descendants(Inv + "invoiceSummary").Single().Element(Inv + "homeCurrency")!;
        home.Element(Typ + "priceHigh")!.Value.ShouldBe("100.00");
        home.Element(Typ + "priceHighVAT")!.Value.ShouldBe("21.00");
        home.Element(Typ + "priceLow")!.Value.ShouldBe("50.00");
        home.Element(Typ + "priceLowVAT")!.Value.ShouldBe("6.00");
        home.Element(Typ + "priceNone")!.Value.ShouldBe("10.00");
        doc.Descendants(Inv + "rateVAT").Select(e => e.Value).ShouldBe(["high", "low", "none"]);
        doc.Descendants(Typ + "numberRequested").Single().Value.ShouldBe("INV1");
        doc.Descendants(Inv + "symVar").Single().Value.ShouldBe("20260001");
    }

    [Theory]
    [InlineData(EDocumentType.CreditNote, "issuedCreditNotice")]
    [InlineData(EDocumentType.Proforma, "issuedProformaInvoice")]
    [InlineData(EDocumentType.Invoice, "issuedInvoice")]
    public void Pohoda_DocumentTypes_MapToValidInvoiceTypes(EDocumentType type, string expected)
    {
        var inv = Issued("X1", ("A", 21m, type == EDocumentType.CreditNote ? -100m : 100m));
        inv.DocumentType = type;

        var bytes = new PohodaAccountingExporter().Export([inv], [], Issuer());

        Parse(bytes).Descendants(Inv + "invoiceType").Single().Value.ShouldBe(expected);
        string.Join(Environment.NewLine, Validate(bytes, PohodaSchemas.Value)).ShouldBeEmpty();
    }

    [Fact]
    public void Pohoda_CreditNote_KeepsNegativeAmounts()
    {
        var cn = Issued("CN1", ("Refund", 21m, -100m));
        cn.DocumentType = EDocumentType.CreditNote;

        var doc = Parse(new PohodaAccountingExporter().Export([cn], [], Issuer()));

        doc.Descendants(Typ + "priceHigh").Single().Value.ShouldBe("-100.00");
        doc.Descendants(Typ + "priceHighVAT").Single().Value.ShouldBe("-21.00");
    }

    [Fact]
    public void Pohoda_ForeignCurrencyWithRate_WritesRateAndAmount_AndValidates()
    {
        var received = Received("R1", "EUR", ("Hosting", 21m, 100m));
        received.ExchangeRate = 24.465m;
        var bytes = new PohodaAccountingExporter().Export([], [received], Issuer());

        var foreign = Parse(bytes).Descendants(Inv + "invoiceSummary").Single().Element(Inv + "foreignCurrency")!;
        foreign.Element(Typ + "rate")!.Value.ShouldBe("24.465");
        foreign.Element(Typ + "amount")!.Value.ShouldBe("1");
        string.Join(Environment.NewLine, Validate(bytes, PohodaSchemas.Value)).ShouldBeEmpty();
    }

    [Fact]
    public void Pohoda_ForeignCurrency_HasNoFabricatedRate_AndValidates()
    {
        var bytes = new PohodaAccountingExporter().Export([], [Received("R1", "EUR", ("Hosting", 21m, 100m))], Issuer());

        var doc = Parse(bytes);
        var foreign = doc.Descendants(Inv + "invoiceSummary").Single().Element(Inv + "foreignCurrency")!;
        foreign.Descendants(Typ + "ids").Single().Value.ShouldBe("EUR");
        foreign.Element(Typ + "priceSum")!.Value.ShouldBe("121.00");
        foreign.Element(Typ + "rate").ShouldBeNull();
        foreign.Element(Typ + "amount").ShouldBeNull();
        string.Join(Environment.NewLine, Validate(bytes, PohodaSchemas.Value)).ShouldBeEmpty();
    }

    // ── Foreign currency with ČNB rate (DEVGUIDE §4.17) ───────────────────────

    private static Invoice IssuedEur(string number, decimal? rate, params (string Text, decimal Vat, decimal Net)[] lines)
    {
        var inv = Issued(number, lines);
        inv.Currency = new Currency { Code = "EUR" };
        inv.ExchangeRate = rate;
        return inv;
    }

    [Fact]
    public void MoneyS3_ForeignCurrencyWithRate_WritesValutyAndCzkSummary_AndValidates()
    {
        var bytes = new MoneyS3AccountingExporter().Export(
            [IssuedEur("INV1", 24.5m, ("A", 21m, 100m), ("B", 0m, 10m))],
            [], Issuer());

        var el = Parse(bytes).Descendants("FaktVyd").Single();
        var mena = el.Element("Valuty")!.Element("Mena")!;
        mena.Element("Kod")!.Value.ShouldBe("EUR");
        mena.Element("Mnozstvi")!.Value.ShouldBe("1");
        mena.Element("Kurs")!.Value.ShouldBe("24.5");
        // Foreign summary in EUR, main summary in CZK (base x rate, VAT from the CZK base).
        el.Element("Valuty")!.Element("Celkem")!.Value.ShouldBe("131.00");
        el.Element("SouhrnDPH")!.Element("Zaklad22")!.Value.ShouldBe("2450.00");
        el.Element("SouhrnDPH")!.Element("DPH22")!.Value.ShouldBe("514.50");
        el.Element("SouhrnDPH")!.Element("Zaklad0")!.Value.ShouldBe("245.00");
        el.Element("Celkem")!.Value.ShouldBe("3209.50");
        // Items carry the price in Valuty, not in Cena.
        el.Descendants("Polozka").ShouldAllBe(p => p.Element("Cena") == null && p.Element("Valuty") != null);
        string.Join(Environment.NewLine, Validate(bytes, MoneySchemas.Value)).ShouldBeEmpty();
    }

    [Fact]
    public void MoneyS3_ForeignReceivedInvoiceWithRate_Validates()
    {
        var received = Received("R1", "EUR", ("Hosting", 21m, 100m));
        received.ExchangeRate = 25m;

        var bytes = new MoneyS3AccountingExporter().Export([], [received], Issuer());

        Parse(bytes).Descendants("FaktPrij").Single().Element("Valuty")!.Element("Mena")!.Element("Kurs")!.Value.ShouldBe("25");
        string.Join(Environment.NewLine, Validate(bytes, MoneySchemas.Value)).ShouldBeEmpty();
    }

    [Fact]
    public void Flexi_ForeignCurrencyWithRate_WritesMenaKurzKurzMnozstvi_AndStaysWritable()
    {
        var received = Received("R1", "EUR", ("Hosting", 21m, 100m));
        received.ExchangeRate = 0.0666m; // e.g. HUF: per ONE unit, so kurzMnozstvi is always 1
        var root = Parse(new AbraFlexiAccountingExporter().Export([IssuedEur("INV1", 24.465m, ("A", 21m, 100m))], [received], Issuer())).Root!;

        var issued = root.Element("faktura-vydana")!;
        issued.Element("mena")!.Value.ShouldBe("code:EUR");
        issued.Element("kurz")!.Value.ShouldBe("24.465");
        issued.Element("kurzMnozstvi")!.Value.ShouldBe("1");
        root.Element("faktura-prijata")!.Element("kurz")!.Value.ShouldBe("0.0666");

        var catalog = FlexiCatalog.Load();
        foreach (var evidence in root.Elements())
            catalog.AssertWritable(evidence);
    }

    [Fact]
    public void Flexi_CzkInvoice_HasNoCurrencyFields()
    {
        var el = Parse(new AbraFlexiAccountingExporter().Export([Issued("INV1", ("A", 21m, 100m))], [], Issuer())).Root!.Element("faktura-vydana")!;
        el.Element("mena").ShouldBeNull();
        el.Element("kurz").ShouldBeNull();
    }

    [Theory]
    [InlineData(EPaymentMethod.BankTransfer, "draft")]
    [InlineData(EPaymentMethod.Cash, "cash")]
    [InlineData(EPaymentMethod.CreditCard, "creditcard")]
    [InlineData(EPaymentMethod.PayPal, null)]
    [InlineData(EPaymentMethod.Other, null)]
    public void Pohoda_PaymentType_UnmappedMethodsOmitTheElement(EPaymentMethod method, string? expected)
    {
        var inv = Issued("X1", ("A", 21m, 10m));
        inv.PaymentMethod = method;

        var bytes = new PohodaAccountingExporter().Export([inv], [], Issuer());

        Parse(bytes).Descendants(Typ + "paymentType").SingleOrDefault()?.Value.ShouldBe(expected);
        if (expected is null) Parse(bytes).Descendants(Inv + "paymentType").ShouldBeEmpty();
        string.Join(Environment.NewLine, Validate(bytes, PohodaSchemas.Value)).ShouldBeEmpty();
    }

    [Fact]
    public void Pohoda_ReceivedInvoice_SupplierNumberGoesToNumberKhDph()
    {
        var doc = Parse(new PohodaAccountingExporter().Export([], [Received("SUP-77", "CZK", ("A", 21m, 10m))], Issuer()));

        var header = doc.Descendants(Inv + "invoiceHeader").Single();
        header.Element(Inv + "invoiceType")!.Value.ShouldBe("receivedInvoice");
        header.Element(Inv + "numberKHDPH")!.Value.ShouldBe("SUP-77");
        header.Element(Inv + "number").ShouldBeNull();
    }

    [Fact]
    public void Pohoda_LongTexts_AreTruncatedToSchemaLengths_AndStayValid()
    {
        var inv = Issued("X1", (new string('x', 300), 21m, 10m));
        inv.Notes = new string('n', 500);
        inv.Client.Address.First().PostalCode = new string('9', 40);

        var bytes = new PohodaAccountingExporter().Export([inv], [], Issuer());

        var doc = Parse(bytes);
        doc.Descendants(Inv + "invoiceHeader").Single().Element(Inv + "text")!.Value.Length.ShouldBe(240);
        doc.Descendants(Inv + "invoiceItem").Single().Element(Inv + "text")!.Value.Length.ShouldBe(90);
        doc.Descendants(Typ + "zip").Single().Value.Length.ShouldBe(15);
        string.Join(Environment.NewLine, Validate(bytes, PohodaSchemas.Value)).ShouldBeEmpty();
    }

    [Fact]
    public void Pohoda_IssuerWithoutIco_Throws()
    {
        var issuer = Issuer();
        issuer.RegistrationNumber = null!;

        Should.Throw<InvalidOperationException>(() => new PohodaAccountingExporter().Export([], [], issuer));
    }

    [Fact]
    public void Pohoda_IsWindows1250Encoded_AndKeepsCzechDiacritics()
    {
        var bytes = new PohodaAccountingExporter().Export([Issued("INV1", ("Příliš žluťoučký kůň", 21m, 100m))], [], Issuer());

        System.Text.Encoding.GetEncoding(1250).GetString(bytes).ShouldContain("Příliš žluťoučký kůň");
        Parse(bytes).Descendants(Inv + "invoiceItem").Single().Element(Inv + "text")!.Value.ShouldBe("Příliš žluťoučký kůň");
    }

    // ── Money S3 ──────────────────────────────────────────────────────────────

    [Fact]
    public void MoneyS3_IssuedAndReceived_ValidateAgainstOfficialXsd()
    {
        var proforma = Issued("PF1", ("A", 21m, 10m));
        proforma.DocumentType = EDocumentType.Proforma;
        var cn = Issued("CN1", ("Refund", 12m, -100m));
        cn.DocumentType = EDocumentType.CreditNote;

        var bytes = new MoneyS3AccountingExporter().Export(
            [Issued("INV1", ("A", 21m, 100m), ("B", 12m, 50m), ("C", 0m, 10m)), proforma, cn],
            [Received("R1", "CZK", ("B", 21m, 200m))], Issuer());

        string.Join(Environment.NewLine, Validate(bytes, MoneySchemas.Value)).ShouldBeEmpty();
    }

    [Fact]
    public void MoneyS3_Totals_AndDocumentKind()
    {
        var proforma = Issued("PF1", ("A", 21m, 10m));
        proforma.DocumentType = EDocumentType.Proforma;

        var doc = Parse(new MoneyS3AccountingExporter().Export(
            [Issued("INV1", ("A", 21m, 100m), ("B", 12m, 50m), ("C", 0m, 10m)), proforma], [], Issuer()));

        var vyd = doc.Root!.Element("SeznamFaktVyd")!.Elements("FaktVyd").ToList();
        vyd[0].Element("Doklad")!.Value.ShouldBe("INV1");
        vyd[0].Element("Druh")!.Value.ShouldBe("N");
        vyd[0].Element("Dobropis")!.Value.ShouldBe("0");
        vyd[0].Element("VarSymbol")!.Value.ShouldBe("20260001");
        vyd[0].Element("Splatno")!.Value.ShouldBe("2026-03-15");
        var summary = vyd[0].Element("SouhrnDPH")!;
        summary.Element("Zaklad22")!.Value.ShouldBe("100.00");
        summary.Element("DPH22")!.Value.ShouldBe("21.00");
        summary.Element("Zaklad5")!.Value.ShouldBe("50.00");
        summary.Element("DPH5")!.Value.ShouldBe("6.00");
        summary.Element("Zaklad0")!.Value.ShouldBe("10.00");
        vyd[0].Element("DodOdb")!.Element("ICO")!.Value.ShouldBe("27082440");
        vyd[1].Element("Druh")!.Value.ShouldBe("F");
    }

    [Fact]
    public void MoneyS3_CreditNote_UsesDobropisFlagWithPositiveAmounts()
    {
        var cn = Issued("CN1", ("Refund", 21m, -100m));
        cn.DocumentType = EDocumentType.CreditNote;

        var el = Parse(new MoneyS3AccountingExporter().Export([cn], [], Issuer())).Descendants("FaktVyd").Single();

        el.Element("Dobropis")!.Value.ShouldBe("1");
        el.Element("SouhrnDPH")!.Element("Zaklad22")!.Value.ShouldBe("100.00");
        el.Descendants("Polozka").Single().Element("Cena")!.Value.ShouldBe("100.00");
        el.Descendants("Polozka").Single().Element("PocetMJ")!.Value.ShouldBe("1.00");
    }

    [Fact]
    public void MoneyS3_ReceivedInvoice_SupplierNumberGoesToPrijatDokl()
    {
        var el = Parse(new MoneyS3AccountingExporter().Export([], [Received("SUP-77", "CZK", ("B", 21m, 200m))], Issuer()))
            .Descendants("FaktPrij").Single();

        el.Element("PrijatDokl")!.Value.ShouldBe("SUP-77");
        el.Element("Doklad").ShouldBeNull();
    }

    // ── ABRA Flexi ────────────────────────────────────────────────────────────

    [Fact]
    public void Flexi_EveryElementExistsInFlexiCatalogue_AndValuesRespectTypesAndLengths()
    {
        var cn = Issued("CN1", ("Refund", 21m, -100m));
        cn.DocumentType = EDocumentType.CreditNote;

        var doc = Parse(new AbraFlexiAccountingExporter().Export(
            [Issued("INV1", ("A", 21m, 100m), ("B", 12m, 50m), ("C", 0m, 10m)), cn],
            [Received("R1", "CZK", ("B", 21m, 200m))], Issuer()));

        var catalog = FlexiCatalog.Load();
        foreach (var evidence in doc.Root!.Elements())
        {
            catalog.AssertWritable(evidence);
            foreach (var item in evidence.Element("polozkyFaktury")!.Elements())
                catalog.AssertWritable(item);
        }
    }

    [Fact]
    public void Flexi_IssuedInvoice_FlatPartnerAndNoComputedSums()
    {
        var el = Parse(new AbraFlexiAccountingExporter().Export(
            [Issued("INV1", ("A", 21m, 100m), ("B", 12m, 50m), ("C", 0m, 10m))], [], Issuer())).Root!.Element("faktura-vydana")!;

        el.Element("kod")!.Value.ShouldBe("INV1");
        el.Element("varSym")!.Value.ShouldBe("20260001");
        el.Element("datVyst")!.Value.ShouldBe("2026-03-01");
        el.Element("ic")!.Value.ShouldBe("27082440");
        el.Element("nazFirmy")!.Value.ShouldBe("Dodavatel s.r.o.");
        el.Element("firma").ShouldBeNull();
        el.Elements().Where(e => e.Name.LocalName.StartsWith("sum")).ShouldBeEmpty();
        el.Descendants("typSzbDphK").Select(e => e.Value)
            .ShouldBe(["typSzbDph.dphZakl", "typSzbDph.dphSniz", "typSzbDph.dphOsv"]);
        el.Descendants("mj").Select(e => e.Value).ShouldAllBe(v => v == "code:KS");
    }

    [Fact]
    public void Flexi_CreditNote_UsesPositiveAmounts_AndReceivedKeepsSupplierNumberInCisDosle()
    {
        var cn = Issued("CN1", ("Refund", 21m, -100m));
        cn.DocumentType = EDocumentType.CreditNote;

        var doc = Parse(new AbraFlexiAccountingExporter().Export([cn], [Received("SUP-77", "CZK", ("B", 21m, 100m))], Issuer()));

        var vydana = doc.Root!.Element("faktura-vydana")!;
        vydana.Element("typDokl")!.Value.ShouldBe("code:DOBROPIS");
        vydana.Descendants("faktura-vydana-polozka").Single().Element("cenaMj")!.Value.ShouldBe("100.00");
        var prijata = doc.Root.Element("faktura-prijata")!;
        prijata.Element("cisDosle")!.Value.ShouldBe("SUP-77");
        prijata.Element("kod").ShouldBeNull();
    }

    [Fact]
    public void Flexi_UnknownUnit_OmitsMjReference()
    {
        var inv = Issued("X1", ("A", 21m, 10m));
        inv.InvoiceItem.First().Unit = "balíček";

        var el = Parse(new AbraFlexiAccountingExporter().Export([inv], [], Issuer())).Descendants("faktura-vydana-polozka").Single();

        el.Element("mj").ShouldBeNull();
    }

    // ── Skipping what cannot be represented ───────────────────────────────────

    [Fact]
    public void CanExport_RejectsDocumentsTheSystemCannotRepresent()
    {
        var pohoda = new PohodaAccountingExporter();
        var money = new MoneyS3AccountingExporter();
        var flexi = new AbraFlexiAccountingExporter();

        var legacyRate = Issued("INV1", ("A", 10m, 100m));
        var dpp = Issued("DPP1", ("A", 21m, 100m)); dpp.DocumentType = EDocumentType.TaxReceiptForAdvance;
        var proforma = Issued("PF1", ("A", 21m, 100m)); proforma.DocumentType = EDocumentType.Proforma;
        var eur = Issued("INV2", ("A", 21m, 100m)); eur.Currency = new Currency { Code = "EUR" };
        var longNumber = Issued("INVOICE-2026-0001", ("A", 21m, 100m));
        var tooLongForPohoda = Issued(new string('N', 21), ("A", 21m, 100m));
        var ok = Issued("INV3", ("A", 21m, 100m));

        foreach (var e in new IAccountingExporter[] { pohoda, money, flexi })
        {
            e.CanExport(legacyRate).ShouldBeFalse(e.System + " legacy 10% rate");
            e.CanExport(dpp).ShouldBeFalse(e.System + " DPP");
            e.CanExport(ok).ShouldBeTrue();
        }
        pohoda.CanExport(proforma).ShouldBeTrue();
        money.CanExport(proforma).ShouldBeTrue();
        flexi.CanExport(proforma).ShouldBeFalse();
        pohoda.CanExport(eur).ShouldBeTrue();
        money.CanExport(eur).ShouldBeFalse(); // foreign currency without a stored ČNB rate
        flexi.CanExport(eur).ShouldBeFalse();
        eur.ExchangeRate = 24.465m;           // ... exportable once the rate exists
        money.CanExport(eur).ShouldBeTrue();
        flexi.CanExport(eur).ShouldBeTrue();
        pohoda.CanExport(longNumber).ShouldBeTrue();
        pohoda.CanExport(tooLongForPohoda).ShouldBeFalse(); // numberRequested is string20
        money.CanExport(longNumber).ShouldBeTrue(); // number goes to EvCisDokl, Doklad is omitted
        flexi.CanExport(longNumber).ShouldBeTrue(); // 17 chars <= 20
        money.CanExport(Received("R1", "EUR", ("A", 21m, 1m))).ShouldBeFalse();
        pohoda.CanExport(Received("R1", "EUR", ("A", 21m, 1m))).ShouldBeTrue();
    }

    [Fact]
    public void XsdValidator_RejectsInvalidPaymentType_SoTheOtherTestsAreMeaningful()
    {
        var inv = Issued("X1", ("A", 21m, 10m));
        inv.PaymentMethod = EPaymentMethod.Cash;
        var cp1250 = System.Text.Encoding.GetEncoding(1250);
        var xml = cp1250.GetString(new PohodaAccountingExporter().Export([inv], [], Issuer())).Replace(">cash<", ">other<");

        Validate(cp1250.GetBytes(xml), PohodaSchemas.Value).ShouldNotBeEmpty();
    }

    [Fact]
    public void Pohoda_LongVariableSymbolUnitIcoDic_AreCutToSchemaLimits_AndValidate()
    {
        var inv = Issued("X1", ("A", 21m, 10m));
        inv.VariableSymbol = new string('1', 40);
        inv.InvoiceItem.First().Unit = "very-long-unit-name";
        inv.Client.RegistrationNumber = new string('9', 30);
        inv.Client.TaxNumber = "CZ" + new string('9', 30);

        var bytes = new PohodaAccountingExporter().Export([inv], [], Issuer());

        var doc = Parse(bytes);
        doc.Descendants(Inv + "symVar").Single().Value.Length.ShouldBe(20);
        doc.Descendants(Inv + "unit").Single().Value.Length.ShouldBe(10);
        doc.Descendants(Typ + "ico").Single().Value.Length.ShouldBe(15);
        doc.Descendants(Typ + "dic").Single().Value.Length.ShouldBe(18);
        string.Join(Environment.NewLine, Validate(bytes, PohodaSchemas.Value)).ShouldBeEmpty();
    }

    [Fact]
    public void MoneyS3_NumberLongerThan10_GoesToEvCisDoklOnly_AndValidates()
    {
        var inv = Issued("INV-2026-001", ("A", 21m, 100m)); // 12 chars
        inv.VariableSymbol = new string('1', 40);

        var bytes = new MoneyS3AccountingExporter().Export([inv], [], Issuer());

        var el = Parse(bytes).Descendants("FaktVyd").Single();
        el.Element("Doklad").ShouldBeNull();
        el.Element("EvCisDokl")!.Value.ShouldBe("INV-2026-001");
        el.Element("VarSymbol")!.Value.Length.ShouldBe(20);
        string.Join(Environment.NewLine, Validate(bytes, MoneySchemas.Value)).ShouldBeEmpty();
    }

    [Fact]
    public void MoneyS3_ShortNumber_WritesBothDokladAndEvCisDokl()
    {
        var el = Parse(new MoneyS3AccountingExporter().Export([Issued("INV1", ("A", 21m, 100m))], [], Issuer())).Descendants("FaktVyd").Single();

        el.Element("Doklad")!.Value.ShouldBe("INV1");
        el.Element("EvCisDokl")!.Value.ShouldBe("INV1");
    }

    [Fact]
    public void AllExporters_EmptyInput_ProduceWellFormedXml()
    {
        IAccountingExporter[] exporters =
            [new PohodaAccountingExporter(), new MoneyS3AccountingExporter(), new AbraFlexiAccountingExporter()];
        foreach (var e in exporters)
            Should.NotThrow(() => Parse(e.Export([], [], Issuer())));
    }

    // ── Request validation (runs before any database access) ──────────────────

    [Fact]
    public async Task Service_RejectsInvertedRange_TooLongRange_AndTooManyIds()
    {
        var service = new AccountingExportService(null!, [new PohodaAccountingExporter()], Substitute.For<ILogger<AccountingExportService>>());
        var march = new DateTime(2026, 3, 1);

        await Should.ThrowAsync<ArgumentException>(() => service.ExportAsync(EAccountingSystem.Pohoda, march.AddDays(5), march, true, true));
        await Should.ThrowAsync<ArgumentException>(() => service.ExportAsync(EAccountingSystem.Pohoda, march, march.AddDays(367), true, true));
        await Should.ThrowAsync<ArgumentException>(() => service.ExportAsync(
            EAccountingSystem.Pohoda, march, march.AddDays(10), true, true, Enumerable.Range(1, 5001).Select(i => (long)i).ToList()));
    }

    // ── Builders ──────────────────────────────────────────────────────────────

    /// <summary>Lines are (description, VAT %, net amount) with quantity 1.</summary>
    private static Invoice Issued(string number, params (string Text, decimal Vat, decimal Net)[] lines)
    {
        var items = lines.Select((l, i) => new InvoiceItem
        {
            OrderIndex = i, Description = l.Text, Quantity = 1, Unit = "ks", UnitPrice = l.Net,
            VatRatePercentage = l.Vat, TotalBeforeVat = l.Net,
            VatAmount = Math.Round(l.Net * l.Vat / 100m, 2), TotalWithVat = l.Net + Math.Round(l.Net * l.Vat / 100m, 2)
        }).ToList();
        return new Invoice
        {
            DocumentType = EDocumentType.Invoice, DocumentNumber = number, VariableSymbol = "20260001",
            IssueDate = new DateTime(2026, 3, 1), DueDate = new DateTime(2026, 3, 15),
            Currency = new Currency { Code = "CZK" }, Client = Partner(), InvoiceItem = items,
            TotalBeforeVat = items.Sum(i => i.TotalBeforeVat), TotalVat = items.Sum(i => i.VatAmount),
            TotalWithVat = items.Sum(i => i.TotalWithVat)
        };
    }

    private static ReceivedInvoice Received(string number, string currency, params (string Text, decimal Vat, decimal Net)[] lines)
    {
        var items = lines.Select((l, i) => new ReceivedInvoiceItem
        {
            OrderIndex = i, Description = l.Text, Quantity = 1, Unit = "ks", UnitPrice = l.Net,
            VatRatePercentage = l.Vat, TotalBeforeVat = l.Net,
            VatAmount = Math.Round(l.Net * l.Vat / 100m, 2), TotalWithVat = l.Net + Math.Round(l.Net * l.Vat / 100m, 2)
        }).ToList();
        return new ReceivedInvoice
        {
            DocumentNumber = number, IssueDate = new DateTime(2026, 3, 2), DueDate = new DateTime(2026, 3, 16),
            Currency = new Currency { Code = currency }, Supplier = Partner(), Items = items,
            TotalBeforeVat = items.Sum(i => i.TotalBeforeVat), TotalVat = items.Sum(i => i.VatAmount),
            TotalWithVat = items.Sum(i => i.TotalWithVat)
        };
    }

    private static Client Partner() => new()
    {
        CompanyName = "Dodavatel s.r.o.", RegistrationNumber = "27082440", TaxNumber = "CZ27082440",
        Address = [new Address { Street = "Hlavní 1", City = "Brno", PostalCode = "60200", IsPrimary = true }]
    };

    private static Client Issuer() => new() { CompanyName = "Fakvio s.r.o.", RegistrationNumber = "11223344", IsIssuer = true };

    /// <summary>Snapshot of the writable fields of the Flexi evidences (Schemas/AbraFlexi/catalog.txt).</summary>
    private sealed class FlexiCatalog
    {
        private readonly Dictionary<string, Dictionary<string, (string Type, int? MaxLength)>> _fields = new();
        private readonly Dictionary<string, HashSet<string>> _selects = new();

        public static FlexiCatalog Load()
        {
            var c = new FlexiCatalog();
            foreach (var line in File.ReadLines(Path.Combine(AppContext.BaseDirectory, "Schemas", "AbraFlexi", "catalog.txt")))
            {
                if (line.StartsWith('#') || line.Length == 0) continue;
                var p = line.Split('\t');
                if (p[2].StartsWith('='))
                    c._selects[$"{p[0]}.{p[1]}"] = p[2][1..].Split(',').ToHashSet();
                else
                {
                    if (!c._fields.TryGetValue(p[0], out var d)) c._fields[p[0]] = d = new();
                    d[p[1]] = (p[2], int.TryParse(p[3], out var ml) ? ml : null);
                }
            }
            return c;
        }

        /// <summary>Every child must be a writable property; strings respect max length; selects use known keys; numerics parse.</summary>
        public void AssertWritable(XElement evidenceElement)
        {
            var evidence = evidenceElement.Name.LocalName;
            foreach (var child in evidenceElement.Elements().Where(e => e.Name.LocalName != "polozkyFaktury"))
            {
                var name = child.Name.LocalName;
                _fields[evidence].ShouldContainKey(name, $"{evidence}.{name} is not a writable Flexi property");
                var (type, maxLength) = _fields[evidence][name];
                if (maxLength is { } max && type == "string") child.Value.Length.ShouldBeLessThanOrEqualTo(max, $"{evidence}.{name}");
                if (type == "numeric") decimal.TryParse(child.Value, System.Globalization.CultureInfo.InvariantCulture, out _).ShouldBeTrue($"{evidence}.{name}");
                if (type == "date") DateTime.TryParseExact(child.Value, "yyyy-MM-dd", null, System.Globalization.DateTimeStyles.None, out _).ShouldBeTrue($"{evidence}.{name}");
                if (type == "select") _selects[$"{evidence}.{name}"].ShouldContain(child.Value, $"{evidence}.{name}");
                if (type == "relation") child.Value.ShouldStartWith("code:", Case.Sensitive, $"{evidence}.{name} must be a code: reference");
            }
        }
    }
}

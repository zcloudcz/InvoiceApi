using System.Xml.Linq;
using Fakvio.Application.Service;
using Fakvio.Domain.Entities;
using Fakvio.Domain.Enums;
using Fakvio.Infrastructure.Service.AccountingExport;
using Shouldly;

namespace Fakvio.Tests.Unit;

/// <summary>
/// Unit tests for the three accounting exporters (Pohoda, Money S3, ABRA Flexi). The exporters
/// are pure functions of in-memory entities, so no database is needed. We assert on the facts an
/// accountant cares about: document number, variable symbol, totals, VAT split, credit-note sign,
/// foreign currency, and that the output is well-formed XML.
/// </summary>
public class AccountingExporterTests
{
    private static readonly XNamespace Inv = "http://www.stormware.cz/schema/version_2/invoice.xsd";
    private static readonly XNamespace Typ = "http://www.stormware.cz/schema/version_2/type.xsd";

    private static XDocument Parse(byte[] bytes) => XDocument.Load(new MemoryStream(bytes));

    // ── Pohoda ────────────────────────────────────────────────────────────────

    [Fact]
    public void Pohoda_IssuedInvoice_MapsHeaderPartnerAndVatBreakdown()
    {
        var doc = Parse(new PohodaAccountingExporter().Export([Issued("INV1", ("A", 21m, 100m), ("B", 12m, 50m), ("C", 0m, 10m))], [], Issuer()));

        var header = doc.Descendants(Inv + "invoiceHeader").Single();
        header.Element(Inv + "invoiceType")!.Value.ShouldBe("issuedInvoice");
        header.Descendants(Typ + "numberRequested").Single().Value.ShouldBe("INV1");
        header.Element(Inv + "symVar")!.Value.ShouldBe("20260001");
        header.Element(Inv + "date")!.Value.ShouldBe("2026-03-01");
        header.Element(Inv + "dateDue")!.Value.ShouldBe("2026-03-15");
        header.Descendants(Typ + "ico").Single().Value.ShouldBe("27082440");
        header.Descendants(Typ + "dic").Single().Value.ShouldBe("CZ27082440");

        var home = doc.Descendants(Inv + "invoiceSummary").Single().Element(Inv + "homeCurrency")!;
        home.Element(Typ + "priceHigh")!.Value.ShouldBe("100.00");
        home.Element(Typ + "priceHighVAT")!.Value.ShouldBe("21.00");
        home.Element(Typ + "priceLow")!.Value.ShouldBe("50.00");
        home.Element(Typ + "priceLowVAT")!.Value.ShouldBe("6.00");
        home.Element(Typ + "priceNone")!.Value.ShouldBe("10.00");
        doc.Descendants(Inv + "rateVAT").Select(e => e.Value).ShouldBe(["high", "low", "none"]);
    }

    [Fact]
    public void Pohoda_CreditNote_UsesCreditNoticeTypeAndKeepsNegativeSign()
    {
        var cn = Issued("CN1", ("Refund", 21m, -100m));
        cn.DocumentType = EDocumentType.CreditNote;

        var doc = Parse(new PohodaAccountingExporter().Export([cn], [], Issuer()));

        doc.Descendants(Inv + "invoiceType").Single().Value.ShouldBe("issuedCreditNotice");
        doc.Descendants(Typ + "priceHigh").Single().Value.ShouldBe("-100.00");
        doc.Descendants(Typ + "priceHighVAT").Single().Value.ShouldBe("-21.00");
    }

    [Fact]
    public void Pohoda_ReceivedInvoice_UsesReceivedType_AndForeignCurrencyBlock()
    {
        var received = Received("R1", "EUR", ("Hosting", 21m, 100m));

        var doc = Parse(new PohodaAccountingExporter().Export([], [received], Issuer()));

        doc.Descendants(Inv + "invoiceType").Single().Value.ShouldBe("receivedInvoice");
        var summary = doc.Descendants(Inv + "invoiceSummary").Single();
        summary.Element(Inv + "homeCurrency").ShouldBeNull();
        var foreign = summary.Element(Inv + "foreignCurrency")!;
        foreign.Descendants(Typ + "ids").Single().Value.ShouldBe("EUR");
        foreign.Element(Typ + "priceSum")!.Value.ShouldBe("121.00");
    }

    [Fact]
    public void Pohoda_ProformaAndPaymentTypes_AreMapped()
    {
        var proforma = Issued("PF1", ("A", 21m, 10m));
        proforma.DocumentType = EDocumentType.Proforma;
        proforma.PaymentMethod = EPaymentMethod.Cash;

        var doc = Parse(new PohodaAccountingExporter().Export([proforma], [], Issuer()));

        doc.Descendants(Inv + "invoiceType").Single().Value.ShouldBe("issuedAdvanceInvoice");
        doc.Descendants(Typ + "paymentType").Single().Value.ShouldBe("cash");
    }

    [Fact]
    public void Pohoda_IsWindows1250Encoded_AndKeepsCzechDiacritics()
    {
        var inv = Issued("INV1", ("Příliš žluťoučký kůň", 21m, 100m));

        var bytes = new PohodaAccountingExporter().Export([inv], [], Issuer());

        System.Text.Encoding.GetEncoding(1250).GetString(bytes).ShouldContain("Příliš žluťoučký kůň");
        Parse(bytes).Descendants(Inv + "text").First().Value.ShouldBe("Příliš žluťoučký kůň");
    }

    // ── Money S3 ──────────────────────────────────────────────────────────────

    [Fact]
    public void MoneyS3_IssuedAndReceived_GoToSeparateLists_WithTotals()
    {
        var doc = Parse(new MoneyS3AccountingExporter().Export(
            [Issued("INV1", ("A", 21m, 100m))], [Received("R1", "CZK", ("B", 21m, 200m))], Issuer()));

        var vyd = doc.Root!.Element("SeznamFaktVyd")!.Element("FaktVyd")!;
        vyd.Element("Doklad")!.Value.ShouldBe("INV1");
        vyd.Element("VarSymbol")!.Value.ShouldBe("20260001");
        vyd.Element("ZaklCelkem")!.Value.ShouldBe("100.00");
        vyd.Element("DphCelkem")!.Value.ShouldBe("21.00");
        vyd.Element("Celkem")!.Value.ShouldBe("121.00");
        vyd.Element("Adresa")!.Element("ICO")!.Value.ShouldBe("27082440");
        vyd.Descendants("Valuty").ShouldBeEmpty();

        var prij = doc.Root.Element("SeznamFaktPrij")!.Element("FaktPrij")!;
        prij.Element("Doklad")!.Value.ShouldBe("R1");
        prij.Element("Celkem")!.Value.ShouldBe("242.00");
    }

    [Fact]
    public void MoneyS3_CreditNote_UsesDobropisAndNegativeAmounts_ForeignCurrencyAddsValuty()
    {
        var cn = Issued("CN1", ("Refund", 21m, -100m));
        cn.DocumentType = EDocumentType.CreditNote;
        cn.Currency = new Currency { Code = "EUR" };

        var doc = Parse(new MoneyS3AccountingExporter().Export([cn], [], Issuer()));

        var el = doc.Descendants("FaktVyd").Single();
        el.Element("TypDokladu")!.Value.ShouldBe("DobropisVyd");
        el.Element("Celkem")!.Value.ShouldBe("-121.00");
        el.Descendants("Valuty").Single().Element("Mena")!.Value.ShouldBe("EUR");
        doc.Root!.Element("SeznamFaktPrij").ShouldBeNull();
    }

    // ── ABRA Flexi ────────────────────────────────────────────────────────────

    [Fact]
    public void Flexi_IssuedInvoice_MapsFieldsAndVatClasses()
    {
        var doc = Parse(new AbraFlexiAccountingExporter().Export(
            [Issued("INV1", ("A", 21m, 100m), ("B", 12m, 50m), ("C", 0m, 10m))], [], Issuer()));

        var el = doc.Root!.Element("faktura-vydana")!;
        el.Element("kod")!.Value.ShouldBe("INV1");
        el.Element("varSym")!.Value.ShouldBe("20260001");
        el.Element("typDokl")!.Value.ShouldBe("code:FAKTURA");
        el.Element("datVyst")!.Value.ShouldBe("2026-03-01");
        el.Element("firma")!.Element("ic")!.Value.ShouldBe("27082440");
        el.Element("mena").ShouldBeNull();
        el.Element("sumCelkem")!.Value.ShouldBe("187.00");
        el.Descendants("typSzbDphK").Select(e => e.Value)
            .ShouldBe(["typSzbDph.zakladni", "typSzbDph.snizena", "typSzbDph.bezDph"]);
    }

    [Fact]
    public void Flexi_ReceivedForeignCurrencyAndCreditNote()
    {
        var cn = Issued("CN1", ("Refund", 21m, -100m));
        cn.DocumentType = EDocumentType.CreditNote;

        var doc = Parse(new AbraFlexiAccountingExporter().Export([cn], [Received("R1", "EUR", ("B", 21m, 100m))], Issuer()));

        doc.Root!.Element("faktura-vydana")!.Element("typDokl")!.Value.ShouldBe("code:DOBROPIS");
        doc.Root.Element("faktura-vydana")!.Element("sumCelkem")!.Value.ShouldBe("-121.00");
        var prijata = doc.Root.Element("faktura-prijata")!;
        prijata.Element("mena")!.Value.ShouldBe("code:EUR");
        prijata.Element("kod")!.Value.ShouldBe("R1");
    }

    [Fact]
    public void AllExporters_EmptyInput_ProduceWellFormedXml()
    {
        IAccountingExporter[] exporters =
            [new PohodaAccountingExporter(), new MoneyS3AccountingExporter(), new AbraFlexiAccountingExporter()];
        foreach (var e in exporters)
            Should.NotThrow(() => Parse(e.Export([], [], Issuer())));
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
}

using System.Text;
using Fakvio.Infrastructure.Service;
using Microsoft.Extensions.Logging;
using NSubstitute;
using Shouldly;

namespace Fakvio.Tests.Unit;

/// <summary>
/// Unit tests for <see cref="UblImportParser"/>.
/// Validates UBL 2.1 / Peppol BIS Billing 3.0 XML parsing into InvoiceExtractedData,
/// including the security hardening at the untrusted-XML trust boundary (F1.10, see
/// docs/adr/0002-sk-einvoicing-peppol.md).
/// </summary>
public class UblImportParserTests
{
    private readonly UblImportParser _sut = new(Substitute.For<ILogger<UblImportParser>>());

    // ─── Real Peppol fixtures (see Ubl/ImportFixtures/README.md for origin/license) ──

    private static byte[] ReadFixture(string fileName)
        => File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "Ubl", "ImportFixtures", fileName));

    [Fact]
    public void Parse_PeppolBaseExampleInvoice_ExtractsAllFields()
    {
        var xml = ReadFixture("base-example.xml");

        var result = _sut.Parse(xml);

        result.ShouldNotBeNull();
        result.DetectedDocumentType.ShouldBe("Invoice");
        result.DocumentNumber.ShouldBe("Snippet1");
        result.IssueDate.ShouldBe(new DateTime(2017, 11, 13, 0, 0, 0, DateTimeKind.Utc));
        result.DueDate.ShouldBe(new DateTime(2017, 12, 1, 0, 0, 0, DateTimeKind.Utc));
        result.TaxableSupplyDate.ShouldBe(new DateTime(2017, 11, 1, 0, 0, 0, DateTimeKind.Utc)); // cac:Delivery/ActualDeliveryDate
        result.Currency.ShouldBe("EUR");

        result.IssuerName.ShouldBe("SupplierOfficialName Ltd");
        result.IssuerRegistrationNumber.ShouldBe("GB983294");
        result.IssuerTaxNumber.ShouldBe("GB1232434");

        result.RecipientName.ShouldBe("Buyer Official Name");
        result.RecipientRegistrationNumber.ShouldBe("39937423947");
        result.RecipientTaxNumber.ShouldBe("SE4598375937");

        result.TotalBeforeVat.ShouldBe(1325m);
        result.TotalVat.ShouldBe(331.25m);
        result.TotalAmount.ShouldBe(1656.25m);

        result.VariableSymbol.ShouldBe("Snippet1");
        result.IBAN.ShouldBe("IBAN32423940");
        result.SWIFT.ShouldBe("BIC324098");
        result.PaymentMethod.ShouldBe("BankTransfer"); // code 30

        result.Items.ShouldNotBeNull();
        result.Items!.Count.ShouldBe(2);

        result.Items[0].Description.ShouldBe("Description of item");
        result.Items[0].Quantity.ShouldBe(7m);
        result.Items[0].UnitPrice.ShouldBe(400m);
        result.Items[0].VatRate.ShouldBe(25.0m);
        result.Items[0].Unit.ShouldBe("DAY");

        // Second line has a negative quantity in the source document itself (an
        // allowance/correction line) — UBL invoices are not sign-normalized on import,
        // unlike CreditNote roots (see the CreditNote test below).
        result.Items[1].Quantity.ShouldBe(-3m);
    }

    [Fact]
    public void Parse_PeppolBaseCreditNote_NegatesQuantitiesAndExtractsFields()
    {
        var xml = ReadFixture("base-creditnote-correction.xml");

        var result = _sut.Parse(xml);

        result.ShouldNotBeNull();
        result.DetectedDocumentType.ShouldBe("CreditNote");
        result.DocumentNumber.ShouldBe("Snippet1");
        result.IssuerRegistrationNumber.ShouldBe("GB983294");
        result.RecipientRegistrationNumber.ShouldBe("39937423947");
        result.TotalAmount.ShouldBe(1656.25m);

        result.Items.ShouldNotBeNull();
        result.Items!.Count.ShouldBe(2);

        // Source document has CreditedQuantity 7 and -3 (Peppol BIS always carries
        // positive amounts on a CreditNote) — Fakvio has no separate credit-note flag
        // on ReceivedInvoice, so quantities are negated on import to represent a credit
        // (see UblImportParser XML doc comment for the rationale).
        result.Items[0].Quantity.ShouldBe(-7m);
        result.Items[1].Quantity.ShouldBe(3m);
    }

    // ─── Hand-crafted structural tests ────────────────────────────────────

    [Fact]
    public void Parse_MinimalInvoice_ExtractsWhatIsAvailable()
    {
        var xml = MinimalInvoiceXml(typeCode: "380");

        var result = _sut.Parse(Encoding.UTF8.GetBytes(xml));

        result.ShouldNotBeNull();
        result.DetectedDocumentType.ShouldBe("Invoice");
        result.DocumentNumber.ShouldBe("MIN-001");
        result.Currency.ShouldBe("EUR");
        result.TotalAmount.ShouldBe(100m);
        result.IssuerRegistrationNumber.ShouldBeNull();
        result.VariableSymbol.ShouldBeNull();
    }

    [Fact]
    public void Parse_AdvanceInvoiceTypeCode386_DetectsTaxReceiptForAdvance()
    {
        var xml = MinimalInvoiceXml(typeCode: "386");

        var result = _sut.Parse(Encoding.UTF8.GetBytes(xml));

        result.ShouldNotBeNull();
        result.DetectedDocumentType.ShouldBe("TaxReceiptForAdvance");
    }

    [Fact]
    public void Parse_UnknownRootElement_ReturnsNull()
    {
        var xml = """<SomethingElse xmlns="urn:example">not a UBL document</SomethingElse>""";

        _sut.Parse(Encoding.UTF8.GetBytes(xml)).ShouldBeNull();
    }

    [Fact]
    public void Parse_IsdocXml_ReturnsNull()
    {
        // An ISDOC document has a completely different namespace/root — must not be
        // mistaken for UBL (the two formats are routed by extension upstream, but the
        // parser itself must also refuse to guess).
        var xml = """<Invoice version="6.0.2" xmlns="http://isdoc.cz/namespace/2013"><ID>X</ID></Invoice>""";

        _sut.Parse(Encoding.UTF8.GetBytes(xml)).ShouldBeNull();
    }

    [Fact]
    public void Parse_EmptyOrNullInput_ReturnsNull()
    {
        _sut.Parse(null!).ShouldBeNull();
        _sut.Parse([]).ShouldBeNull();
    }

    [Fact]
    public void Parse_MalformedXml_ReturnsNullNotThrows()
    {
        var xml = "<Invoice xmlns=\"urn:oasis:names:specification:ubl:schema:xsd:Invoice-2\">"; // unclosed tag

        _sut.Parse(Encoding.UTF8.GetBytes(xml)).ShouldBeNull();
    }

    // ─── Security: XXE / entity-expansion / oversized payload ────────────

    [Fact]
    public void Parse_DocumentWithDoctype_ReturnsNullNotThrows()
    {
        // Classic XXE shape: a DOCTYPE with an external entity that would read a local
        // file if resolved. DtdProcessing.Prohibit must reject this before the entity
        // is ever looked at — the exact attack payload doesn't matter, only that any
        // DOCTYPE is refused outright.
        var xml = """
            <?xml version="1.0"?>
            <!DOCTYPE Invoice [
              <!ENTITY xxe SYSTEM "file:///etc/passwd">
            ]>
            <Invoice xmlns="urn:oasis:names:specification:ubl:schema:xsd:Invoice-2">
              <ID>&xxe;</ID>
            </Invoice>
            """;

        _sut.Parse(Encoding.UTF8.GetBytes(xml)).ShouldBeNull();
    }

    [Fact]
    public void Parse_BillionLaughsDoctype_ReturnsNullNotThrows()
    {
        // Entity-bomb shape (exponential expansion via nested entities) — also blocked
        // purely by rejecting the DOCTYPE, before any entity is expanded.
        var xml = """
            <?xml version="1.0"?>
            <!DOCTYPE lolz [
              <!ENTITY lol "lol">
              <!ENTITY lol2 "&lol;&lol;&lol;&lol;&lol;&lol;&lol;&lol;&lol;&lol;">
            ]>
            <Invoice xmlns="urn:oasis:names:specification:ubl:schema:xsd:Invoice-2">
              <ID>&lol2;</ID>
            </Invoice>
            """;

        _sut.Parse(Encoding.UTF8.GetBytes(xml)).ShouldBeNull();
    }

    [Fact]
    public void Parse_OversizedPayload_ReturnsNullWithoutParsing()
    {
        // A well-formed but too-large document — rejected by the byte-size guard
        // before XmlReader even sees it.
        var padding = new string('x', UblImportParser.MaxXmlSizeBytes + 1);
        var xml = $"""<Invoice xmlns="urn:oasis:names:specification:ubl:schema:xsd:Invoice-2"><cbc:Note xmlns:cbc="urn:oasis:names:specification:ubl:schema:xsd:CommonBasicComponents-2">{padding}</cbc:Note></Invoice>""";

        _sut.Parse(Encoding.UTF8.GetBytes(xml)).ShouldBeNull();
    }

    // ─── Helpers ───────────────────────────────────────────────────────────

    private static string MinimalInvoiceXml(string typeCode) => $"""
        <Invoice xmlns:cac="urn:oasis:names:specification:ubl:schema:xsd:CommonAggregateComponents-2"
            xmlns:cbc="urn:oasis:names:specification:ubl:schema:xsd:CommonBasicComponents-2"
            xmlns="urn:oasis:names:specification:ubl:schema:xsd:Invoice-2">
            <cbc:ID>MIN-001</cbc:ID>
            <cbc:IssueDate>2026-01-01</cbc:IssueDate>
            <cbc:InvoiceTypeCode>{typeCode}</cbc:InvoiceTypeCode>
            <cbc:DocumentCurrencyCode>EUR</cbc:DocumentCurrencyCode>
            <cac:LegalMonetaryTotal>
                <cbc:TaxExclusiveAmount currencyID="EUR">100</cbc:TaxExclusiveAmount>
                <cbc:PayableAmount currencyID="EUR">100</cbc:PayableAmount>
            </cac:LegalMonetaryTotal>
            <cac:InvoiceLine>
                <cbc:ID>1</cbc:ID>
                <cbc:InvoicedQuantity unitCode="C62">1</cbc:InvoicedQuantity>
                <cac:Item>
                    <cbc:Description>Simple item</cbc:Description>
                </cac:Item>
                <cac:Price>
                    <cbc:PriceAmount currencyID="EUR">100</cbc:PriceAmount>
                </cac:Price>
            </cac:InvoiceLine>
        </Invoice>
        """;
}

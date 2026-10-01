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

    // ─── Field mapping refinements (Codex review follow-ups) ─────────────

    [Fact]
    public void Parse_TaxPointDatePresent_PreferredOverActualDeliveryDate()
    {
        // BT-7 (TaxPointDate) is the direct match for "date of taxable supply" (DUZP) —
        // it must win over the Delivery/ActualDeliveryDate (BT-72) fallback when present.
        var xml = $$"""
            <Invoice xmlns:cac="urn:oasis:names:specification:ubl:schema:xsd:CommonAggregateComponents-2"
                xmlns:cbc="urn:oasis:names:specification:ubl:schema:xsd:CommonBasicComponents-2"
                xmlns="urn:oasis:names:specification:ubl:schema:xsd:Invoice-2">
                <cbc:ID>TP-1</cbc:ID>
                <cbc:IssueDate>2026-01-01</cbc:IssueDate>
                <cbc:TaxPointDate>2026-01-10</cbc:TaxPointDate>
                <cbc:InvoiceTypeCode>380</cbc:InvoiceTypeCode>
                <cbc:DocumentCurrencyCode>EUR</cbc:DocumentCurrencyCode>
                <cac:Delivery><cbc:ActualDeliveryDate>2026-01-20</cbc:ActualDeliveryDate></cac:Delivery>
                <cac:LegalMonetaryTotal>
                    <cbc:TaxExclusiveAmount currencyID="EUR">100</cbc:TaxExclusiveAmount>
                    <cbc:PayableAmount currencyID="EUR">100</cbc:PayableAmount>
                </cac:LegalMonetaryTotal>
            </Invoice>
            """;

        var result = _sut.Parse(Encoding.UTF8.GetBytes(xml));

        result.ShouldNotBeNull();
        result.TaxableSupplyDate.ShouldBe(new DateTime(2026, 1, 10, 0, 0, 0, DateTimeKind.Utc));
    }

    [Fact]
    public void Parse_CreditNoteDueDate_ReadFromPaymentMeansNotRoot()
    {
        // CreditNote-2's UBL schema has no root cbc:DueDate at all — only Invoice-2 does.
        var xml = """
            <CreditNote xmlns:cac="urn:oasis:names:specification:ubl:schema:xsd:CommonAggregateComponents-2"
                xmlns:cbc="urn:oasis:names:specification:ubl:schema:xsd:CommonBasicComponents-2"
                xmlns="urn:oasis:names:specification:ubl:schema:xsd:CreditNote-2">
                <cbc:ID>CN-1</cbc:ID>
                <cbc:IssueDate>2026-01-01</cbc:IssueDate>
                <cbc:CreditNoteTypeCode>381</cbc:CreditNoteTypeCode>
                <cbc:DocumentCurrencyCode>EUR</cbc:DocumentCurrencyCode>
                <cac:PaymentMeans>
                    <cbc:PaymentMeansCode>30</cbc:PaymentMeansCode>
                    <cbc:PaymentDueDate>2026-01-15</cbc:PaymentDueDate>
                </cac:PaymentMeans>
                <cac:LegalMonetaryTotal>
                    <cbc:PayableAmount currencyID="EUR">50</cbc:PayableAmount>
                </cac:LegalMonetaryTotal>
            </CreditNote>
            """;

        var result = _sut.Parse(Encoding.UTF8.GetBytes(xml));

        result.ShouldNotBeNull();
        result.DueDate.ShouldBe(new DateTime(2026, 1, 15, 0, 0, 0, DateTimeKind.Utc));
    }

    [Fact]
    public void Parse_LineWithBaseQuantityAndPriceAmount_UnitPriceComesFromLineExtensionAmount()
    {
        // Price/PriceAmount here is "per 10 units" (BaseQuantity=10) — naively using it as
        // the per-unit price would import a price 10x too high. LineExtensionAmount / Quantity
        // must be used instead: 240 / 3 = 80 per unit, matching what the line actually bills.
        var xml = MinimalInvoiceWithLine(quantity: "3", lineExtensionAmount: "240", priceAmount: "800", baseQuantity: "10");

        var result = _sut.Parse(Encoding.UTF8.GetBytes(xml));

        result.ShouldNotBeNull();
        result.Items![0].UnitPrice.ShouldBe(80m);
    }

    [Fact]
    public void Parse_TwoTaxTotals_PicksTheOneMatchingDocumentCurrency()
    {
        // A non-EUR seller may legally report VAT in both the document currency and the
        // seller's accounting currency (BT-6/BT-111) as two separate TaxTotal elements.
        var xml = """
            <Invoice xmlns:cac="urn:oasis:names:specification:ubl:schema:xsd:CommonAggregateComponents-2"
                xmlns:cbc="urn:oasis:names:specification:ubl:schema:xsd:CommonBasicComponents-2"
                xmlns="urn:oasis:names:specification:ubl:schema:xsd:Invoice-2">
                <cbc:ID>TT-1</cbc:ID>
                <cbc:IssueDate>2026-01-01</cbc:IssueDate>
                <cbc:InvoiceTypeCode>380</cbc:InvoiceTypeCode>
                <cbc:DocumentCurrencyCode>CZK</cbc:DocumentCurrencyCode>
                <cac:TaxTotal><cbc:TaxAmount currencyID="EUR">21.00</cbc:TaxAmount></cac:TaxTotal>
                <cac:TaxTotal><cbc:TaxAmount currencyID="CZK">525.00</cbc:TaxAmount></cac:TaxTotal>
                <cac:LegalMonetaryTotal>
                    <cbc:PayableAmount currencyID="CZK">2500</cbc:PayableAmount>
                </cac:LegalMonetaryTotal>
            </Invoice>
            """;

        var result = _sut.Parse(Encoding.UTF8.GetBytes(xml));

        result.ShouldNotBeNull();
        result.TotalVat.ShouldBe(525.00m);
    }

    [Fact]
    public void Parse_TwoPartyTaxSchemes_PicksTheVatOne()
    {
        var xml = """
            <Invoice xmlns:cac="urn:oasis:names:specification:ubl:schema:xsd:CommonAggregateComponents-2"
                xmlns:cbc="urn:oasis:names:specification:ubl:schema:xsd:CommonBasicComponents-2"
                xmlns="urn:oasis:names:specification:ubl:schema:xsd:Invoice-2">
                <cbc:ID>PTS-1</cbc:ID>
                <cbc:IssueDate>2026-01-01</cbc:IssueDate>
                <cbc:InvoiceTypeCode>380</cbc:InvoiceTypeCode>
                <cbc:DocumentCurrencyCode>EUR</cbc:DocumentCurrencyCode>
                <cac:AccountingSupplierParty>
                    <cac:Party>
                        <cac:PartyTaxScheme>
                            <cbc:CompanyID>FR00000000</cbc:CompanyID>
                            <cac:TaxScheme><cbc:ID>FC</cbc:ID></cac:TaxScheme>
                        </cac:PartyTaxScheme>
                        <cac:PartyTaxScheme>
                            <cbc:CompanyID>SK2020123456</cbc:CompanyID>
                            <cac:TaxScheme><cbc:ID>VAT</cbc:ID></cac:TaxScheme>
                        </cac:PartyTaxScheme>
                    </cac:Party>
                </cac:AccountingSupplierParty>
                <cac:LegalMonetaryTotal>
                    <cbc:PayableAmount currencyID="EUR">100</cbc:PayableAmount>
                </cac:LegalMonetaryTotal>
            </Invoice>
            """;

        var result = _sut.Parse(Encoding.UTF8.GetBytes(xml));

        result.ShouldNotBeNull();
        result.IssuerTaxNumber.ShouldBe("SK2020123456");
    }

    [Theory]
    [InlineData("100.50", 100.50)]
    [InlineData("-3", -3)]
    [InlineData("25.0", 25.0)]
    public void ParseDecimal_ValidUblForms_ParsesCorrectly(string input, double expected)
    {
        var xml = MinimalInvoiceWithLine(quantity: input, lineExtensionAmount: null, priceAmount: input, baseQuantity: null);

        var result = _sut.Parse(Encoding.UTF8.GetBytes(xml));

        result.ShouldNotBeNull();
        result.Items![0].UnitPrice.ShouldBe((decimal)expected);
    }

    [Fact]
    public void ParseDecimal_ThousandsSeparatorForm_RejectedRatherThanMisparsed()
    {
        // "1,23" is not a valid UBL/Peppol decimal (InvariantCulture only, no grouping).
        // NumberStyles.Any would silently read this as 123 -- it must be rejected instead.
        var xml = MinimalInvoiceWithLine(quantity: "1", lineExtensionAmount: null, priceAmount: "1,23", baseQuantity: null);

        var result = _sut.Parse(Encoding.UTF8.GetBytes(xml));

        result.ShouldNotBeNull();
        result.Items![0].UnitPrice.ShouldBeNull();
    }

    [Fact]
    public void Parse_DateWithTimezoneOffset_ParsesJustTheDatePart()
    {
        var xml = MinimalInvoiceXml(typeCode: "380").Replace("2026-01-01", "2026-01-01+02:00");

        var result = _sut.Parse(Encoding.UTF8.GetBytes(xml));

        result.ShouldNotBeNull();
        result.IssueDate.ShouldBe(new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc));
    }

    [Fact]
    public void Parse_NonIsoDateForm_ReturnsNullRatherThanGuessing()
    {
        var xml = MinimalInvoiceXml(typeCode: "380").Replace("2026-01-01", "01/02/2026");

        var result = _sut.Parse(Encoding.UTF8.GetBytes(xml));

        result.ShouldNotBeNull();
        result.IssueDate.ShouldBeNull();
    }

    [Fact]
    public void Parse_DateWithTrailingGarbage_ReturnsNullRatherThanTruncating()
    {
        // A naive "take the first 10 characters" implementation would happily accept this
        // as 2026-01-01 -- the whole string must be validated, not just a prefix.
        var xml = MinimalInvoiceXml(typeCode: "380").Replace("2026-01-01", "2026-01-01garbage");

        var result = _sut.Parse(Encoding.UTF8.GetBytes(xml));

        result.ShouldNotBeNull();
        result.IssueDate.ShouldBeNull();
    }

    [Fact]
    public void Parse_QuantityWithSchemaValidWhitespace_ParsesCorrectly()
    {
        // xsd:decimal's whitespace facet is "collapse" -- leading/trailing whitespace
        // around a numeric value is schema-valid and must still parse.
        var xml = MinimalInvoiceWithLine(quantity: " 3 ", lineExtensionAmount: "300", priceAmount: "100", baseQuantity: null);

        var result = _sut.Parse(Encoding.UTF8.GetBytes(xml));

        result.ShouldNotBeNull();
        result.Items![0].Quantity.ShouldBe(3m);
    }

    [Fact]
    public void Parse_CreditNoteWithMultiplePaymentMeans_FindsDueDateOnEither()
    {
        // A document can legally carry more than one PaymentMeans; only one of them may
        // carry a PaymentDueDate -- it must not be assumed to be the first.
        var xml = """
            <CreditNote xmlns:cac="urn:oasis:names:specification:ubl:schema:xsd:CommonAggregateComponents-2"
                xmlns:cbc="urn:oasis:names:specification:ubl:schema:xsd:CommonBasicComponents-2"
                xmlns="urn:oasis:names:specification:ubl:schema:xsd:CreditNote-2">
                <cbc:ID>CN-2</cbc:ID>
                <cbc:IssueDate>2026-01-01</cbc:IssueDate>
                <cbc:CreditNoteTypeCode>381</cbc:CreditNoteTypeCode>
                <cbc:DocumentCurrencyCode>EUR</cbc:DocumentCurrencyCode>
                <cac:PaymentMeans><cbc:PaymentMeansCode>30</cbc:PaymentMeansCode></cac:PaymentMeans>
                <cac:PaymentMeans>
                    <cbc:PaymentMeansCode>58</cbc:PaymentMeansCode>
                    <cbc:PaymentDueDate>2026-02-01</cbc:PaymentDueDate>
                </cac:PaymentMeans>
                <cac:LegalMonetaryTotal>
                    <cbc:PayableAmount currencyID="EUR">50</cbc:PayableAmount>
                </cac:LegalMonetaryTotal>
            </CreditNote>
            """;

        var result = _sut.Parse(Encoding.UTF8.GetBytes(xml));

        result.ShouldNotBeNull();
        result.DueDate.ShouldBe(new DateTime(2026, 2, 1, 0, 0, 0, DateTimeKind.Utc));
    }

    // ─── Helpers ───────────────────────────────────────────────────────────

    /// <summary>
    /// Builds a minimal single-line invoice for testing line-level price/quantity parsing.
    /// <paramref name="lineExtensionAmount"/> and <paramref name="baseQuantity"/> are omitted
    /// from the XML entirely when null, so a test can isolate the PriceAmount-only fallback.
    /// </summary>
    private static string MinimalInvoiceWithLine(
        string quantity, string? lineExtensionAmount, string? priceAmount, string? baseQuantity)
    {
        var lineExtensionXml = lineExtensionAmount != null
            ? $"""<cbc:LineExtensionAmount currencyID="EUR">{lineExtensionAmount}</cbc:LineExtensionAmount>"""
            : "";
        var baseQuantityXml = baseQuantity != null
            ? $"<cbc:BaseQuantity>{baseQuantity}</cbc:BaseQuantity>"
            : "";

        return $"""
            <Invoice xmlns:cac="urn:oasis:names:specification:ubl:schema:xsd:CommonAggregateComponents-2"
                xmlns:cbc="urn:oasis:names:specification:ubl:schema:xsd:CommonBasicComponents-2"
                xmlns="urn:oasis:names:specification:ubl:schema:xsd:Invoice-2">
                <cbc:ID>LN-1</cbc:ID>
                <cbc:IssueDate>2026-01-01</cbc:IssueDate>
                <cbc:InvoiceTypeCode>380</cbc:InvoiceTypeCode>
                <cbc:DocumentCurrencyCode>EUR</cbc:DocumentCurrencyCode>
                <cac:LegalMonetaryTotal>
                    <cbc:PayableAmount currencyID="EUR">100</cbc:PayableAmount>
                </cac:LegalMonetaryTotal>
                <cac:InvoiceLine>
                    <cbc:ID>1</cbc:ID>
                    <cbc:InvoicedQuantity unitCode="C62">{quantity}</cbc:InvoicedQuantity>
                    {lineExtensionXml}
                    <cac:Item><cbc:Description>Item</cbc:Description></cac:Item>
                    <cac:Price>
                        <cbc:PriceAmount currencyID="EUR">{priceAmount}</cbc:PriceAmount>
                        {baseQuantityXml}
                    </cac:Price>
                </cac:InvoiceLine>
            </Invoice>
            """;
    }

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

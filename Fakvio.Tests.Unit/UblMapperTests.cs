using System.Globalization;
using Fakvio.Domain.Entities;
using Fakvio.Domain.Enums;
using Fakvio.Infrastructure.Service.Ubl;
using Fakvio.Tests.Unit.Ubl;
using Shouldly;

namespace Fakvio.Tests.Unit;

/// <summary>
/// Unit tests for <c>UblMapper</c> (ADR 0002, F1.3 + F1.4). Mirrors <see cref="IsdocExportServiceTests"/>
/// in spirit but needs no database — <c>UblMapper.Map</c> is a pure function of an in-memory
/// <see cref="Invoice"/> graph.
///
/// Every "should be a valid Peppol document" assertion runs the mapped XML through
/// <see cref="UblTestValidator"/> (F1.2) — both the XSD and the CEN/Peppol schematron. A test
/// that only inspects individual XElements without also validating would miss a wrong element
/// order (schema-invalid) or a rule violation (BR-*/ PEPPOL-EN16931-*) that individual
/// assertions cannot see.
/// </summary>
public class UblMapperTests
{
    // --------------------------------------------------------------------------
    // CZ issuer -> SK client, two VAT rates, IBAN, variable symbol
    // --------------------------------------------------------------------------

    [Fact]
    public void Map_CzIssuerToSkClient_TwoVatRates_IsValidPeppolInvoice()
    {
        var invoice = BuildInvoice(
            issuer: CzIssuer(),
            client: SkClient(),
            currencyCode: "EUR",
            items:
            [
                Item(1, "Web development", 10, 100m, 21m),
                Item(2, "Hosting", 1, 50m, 15m)
            ]);

        var document = UblMapper.Map(invoice);

        UblTestValidator.ValidateXsd(document).ShouldBeEmpty();
        UblTestValidator.ValidateSchematron(document).ShouldBeEmpty();
    }

    // --------------------------------------------------------------------------
    // SK issuer -> SK client, EUR, both endpoint IDs derived as 0245:<DIC>
    // --------------------------------------------------------------------------

    [Fact]
    public void Map_SkIssuerToSkClient_Eur_UsesScheme0245_AndIsValid()
    {
        var invoice = BuildInvoice(
            issuer: SkIssuer(),
            client: SkClient(),
            currencyCode: "EUR",
            items: [Item(1, "Consulting", 5, 200m, 20m)]);

        var document = UblMapper.Map(invoice);

        var cbc = UblMapper.CbcNs;
        var cac = UblMapper.CacNs;
        var supplierEndpoint = document.Root!
            .Element(cac + "AccountingSupplierParty")!.Element(cac + "Party")!.Element(cbc + "EndpointID")!;
        supplierEndpoint.Attribute("schemeID")!.Value.ShouldBe("0245");
        supplierEndpoint.Value.ShouldBe("2020123456");

        UblTestValidator.ValidateXsd(document).ShouldBeEmpty();
        UblTestValidator.ValidateSchematron(document).ShouldBeEmpty();
    }

    // --------------------------------------------------------------------------
    // Document type mapping
    // --------------------------------------------------------------------------

    [Fact]
    public void Map_TaxReceiptForAdvance_UsesInvoiceTypeCode386()
    {
        var invoice = BuildInvoice(CzIssuer(), SkClient(), "EUR", [Item(1, "Advance", 1, 100m, 21m)]);
        invoice.DocumentType = EDocumentType.TaxReceiptForAdvance;

        var document = UblMapper.Map(invoice);

        document.Root!.Element(UblMapper.CbcNs + "InvoiceTypeCode")!.Value.ShouldBe("386");
        UblTestValidator.ValidateXsd(document).ShouldBeEmpty();
        UblTestValidator.ValidateSchematron(document).ShouldBeEmpty();
    }

    [Fact]
    public void Map_Proforma_Throws()
    {
        var invoice = BuildInvoice(CzIssuer(), SkClient(), "EUR", [Item(1, "Deposit", 1, 100m, 21m)]);
        invoice.DocumentType = EDocumentType.Proforma;

        Should.Throw<InvalidOperationException>(() => UblMapper.Map(invoice));
    }

    // --------------------------------------------------------------------------
    // Totals (BR-CO-10..17)
    // --------------------------------------------------------------------------

    [Fact]
    public void Map_Totals_AreComputedFromLines_AwayFromZeroRounding()
    {
        // 3 x 10.005 = 30.015 before VAT, at 21% -> VAT = 6.30315 -> rounds to 6.30 (away from
        // zero) — picked so the naive Math.Round(MidpointRounding.ToEven) default would give a
        // different (wrong, per BR-CO-17) result, making a silent rounding-mode regression fail.
        var invoice = BuildInvoice(CzIssuer(), SkClient(), "EUR", [Item(1, "Item", 3, 10.005m, 21m)]);

        var document = UblMapper.Map(invoice);

        var cac = UblMapper.CacNs;
        var cbc = UblMapper.CbcNs;
        var taxAmount = document.Root!.Element(cac + "TaxTotal")!.Element(cbc + "TaxAmount")!.Value;
        taxAmount.ShouldBe("6.30");

        UblTestValidator.ValidateXsd(document).ShouldBeEmpty();
        UblTestValidator.ValidateSchematron(document).ShouldBeEmpty();
    }

    // --------------------------------------------------------------------------
    // Text rows
    // --------------------------------------------------------------------------

    [Fact]
    public void Map_TextRow_BecomesNote_NotAnInvoiceLine()
    {
        var invoice = BuildInvoice(CzIssuer(), SkClient(), "EUR",
        [
            new InvoiceItem
            {
                OrderIndex = 1, IsTextRow = true, Description = "Práce provedeny dle smlouvy č. 123"
            },
            Item(2, "Real line", 1, 100m, 21m)
        ]);

        var document = UblMapper.Map(invoice);

        var cac = UblMapper.CacNs;
        var cbc = UblMapper.CbcNs;
        document.Root!.Elements(cbc + "Note").ShouldContain(e => e.Value == "Práce provedeny dle smlouvy č. 123");
        document.Root!.Elements(cac + "InvoiceLine").Count().ShouldBe(1);

        UblTestValidator.ValidateXsd(document).ShouldBeEmpty();
        UblTestValidator.ValidateSchematron(document).ShouldBeEmpty();
    }

    // --------------------------------------------------------------------------
    // Credit note (F1.4)
    // --------------------------------------------------------------------------

    [Fact]
    public void Map_CreditNote_UsesCreditNoteRoot_AndBillingReference_WithPositiveAmounts()
    {
        // Rows stored with a negative unit price (one of the sign conventions a user might
        // enter for a credit note) -> TotalWithVat ends up negative -> mapper must flip
        // everything to positive amounts in the UBL output.
        var invoice = BuildInvoice(CzIssuer(), SkClient(), "EUR", [Item(1, "Returned goods", 2, -100m, 21m)]);
        invoice.DocumentType = EDocumentType.CreditNote;
        invoice.DocumentNumber = "CN2026001";

        var document = UblMapper.Map(invoice, precedingDocumentNumbers: ["INV2026001"]);

        document.Root!.Name.LocalName.ShouldBe("CreditNote");
        document.Root!.Element(UblMapper.CbcNs + "CreditNoteTypeCode")!.Value.ShouldBe("381");

        var cac = UblMapper.CacNs;
        var cbc = UblMapper.CbcNs;
        var line = document.Root!.Element(cac + "CreditNoteLine")!;
        decimal.Parse(line.Element(cbc + "CreditedQuantity")!.Value, CultureInfo.InvariantCulture).ShouldBe(2m);
        decimal.Parse(line.Element(cbc + "LineExtensionAmount")!.Value, CultureInfo.InvariantCulture).ShouldBe(200m);
        var priceAmount = line.Element(cac + "Price")!.Element(cbc + "PriceAmount")!.Value;
        decimal.Parse(priceAmount, CultureInfo.InvariantCulture).ShouldBe(100m);

        var billingReference = document.Root!.Element(cac + "BillingReference")!
            .Element(cac + "InvoiceDocumentReference")!.Element(cbc + "ID")!.Value;
        billingReference.ShouldBe("INV2026001");

        UblTestValidator.ValidateXsd(document).ShouldBeEmpty();
        UblTestValidator.ValidateSchematron(document).ShouldBeEmpty();
    }

    // --------------------------------------------------------------------------
    // Final invoice with an advance-payment deduction row (F1.4)
    // --------------------------------------------------------------------------

    [Fact]
    public void Map_FinalInvoiceWithAdvanceDeduction_NegatesQuantity_KeepsPricePositive()
    {
        // Mirrors InvoiceService's deduction row: Quantity = 1, UnitPrice = -deductionBase.
        var deductionRow = Item(2, "Odečet přijaté zálohy / Advance payment deduction", 1, -200m, 21m);
        var invoice = BuildInvoice(CzIssuer(), SkClient(), "EUR",
            [Item(1, "Web development", 1, 1000m, 21m), deductionRow]);

        var document = UblMapper.Map(invoice, precedingDocumentNumbers: ["TR2026001"]);

        var cac = UblMapper.CacNs;
        var cbc = UblMapper.CbcNs;
        var deductionLine = document.Root!.Elements(cac + "InvoiceLine").Last();
        decimal.Parse(deductionLine.Element(cbc + "InvoicedQuantity")!.Value, CultureInfo.InvariantCulture).ShouldBe(-1m);
        var priceAmount = deductionLine.Element(cac + "Price")!.Element(cbc + "PriceAmount")!.Value;
        decimal.Parse(priceAmount, CultureInfo.InvariantCulture).ShouldBe(200m);
        decimal.Parse(deductionLine.Element(cbc + "LineExtensionAmount")!.Value, CultureInfo.InvariantCulture).ShouldBe(-200m);

        var billingReference = document.Root!.Element(cac + "BillingReference")!
            .Element(cac + "InvoiceDocumentReference")!.Element(cbc + "ID")!.Value;
        billingReference.ShouldBe("TR2026001");

        UblTestValidator.ValidateXsd(document).ShouldBeEmpty();
        UblTestValidator.ValidateSchematron(document).ShouldBeEmpty();
    }

    // --------------------------------------------------------------------------
    // Non-VAT-payer issuer (F1.4)
    // --------------------------------------------------------------------------

    [Fact]
    public void Map_NonVatPayerIssuer_AllLinesCategoryO_NoPartyTaxScheme()
    {
        // A non-VAT-payer still needs a derivable Peppol endpoint ID to be a valid document
        // (PEPPOL-EN16931-R020) — an SK non-payer's bare 10-digit DIC still derives scheme 0245
        // (ADR 0002 §4.1.2), it just carries no "SK" prefix because that prefix denotes IC DPH
        // (VAT registration), which a non-payer does not have.
        var issuer = SkIssuer();
        issuer.IsVatPayer = false;
        issuer.TaxNumber = "2020123456";
        var invoice = BuildInvoice(issuer, SkClient(), "EUR", [Item(1, "Consulting", 1, 500m, 0m)]);

        var document = UblMapper.Map(invoice);

        var cac = UblMapper.CacNs;
        var cbc = UblMapper.CbcNs;
        document.Root!.Descendants(cac + "PartyTaxScheme").ShouldBeEmpty();
        document.Root!.Element(cac + "InvoiceLine")!.Element(cac + "Item")!
            .Element(cac + "ClassifiedTaxCategory")!.Element(cbc + "ID")!.Value.ShouldBe("O");
        document.Root!.Element(cac + "TaxTotal")!.Element(cac + "TaxSubtotal")!
            .Element(cac + "TaxCategory")!.Element(cbc + "ID")!.Value.ShouldBe("O");

        UblTestValidator.ValidateXsd(document).ShouldBeEmpty();
        UblTestValidator.ValidateSchematron(document).ShouldBeEmpty();
    }

    // --------------------------------------------------------------------------
    // Reverse charge / PDP (F1.4)
    // --------------------------------------------------------------------------

    [Fact]
    public void Map_ReverseCharge_CategoryAe_BothPartiesCarryVatId()
    {
        var item = Item(1, "Construction work", 1, 1000m, 0m);
        item.VatRegime = EVatRegime.ReverseCharge;
        var invoice = BuildInvoice(CzIssuer(), SkClient(), "EUR", [item]);

        var document = UblMapper.Map(invoice);

        var cac = UblMapper.CacNs;
        var cbc = UblMapper.CbcNs;
        document.Root!.Descendants(cac + "PartyTaxScheme").Count().ShouldBe(2);
        document.Root!.Element(cac + "InvoiceLine")!.Element(cac + "Item")!
            .Element(cac + "ClassifiedTaxCategory")!.Element(cbc + "ID")!.Value.ShouldBe("AE");
        var headerCategory = document.Root!.Element(cac + "TaxTotal")!.Element(cac + "TaxSubtotal")!.Element(cac + "TaxCategory")!;
        headerCategory.Element(cbc + "ID")!.Value.ShouldBe("AE");
        headerCategory.Element(cbc + "TaxExemptionReasonCode")!.Value.ShouldBe("VATEX-EU-AE");

        UblTestValidator.ValidateXsd(document).ShouldBeEmpty();
        UblTestValidator.ValidateSchematron(document).ShouldBeEmpty();
    }

    // --------------------------------------------------------------------------
    // Codex round-1 fixes: fractional quantity, VAT-payer all-OutOfScope, mixed-sign
    // credit note, blank IBAN, unprefixed SK VAT ID
    // --------------------------------------------------------------------------

    [Fact]
    public void Map_FractionalQuantity_KeepsFourDecimals_NotRoundedToMoneyPrecision()
    {
        // 0.3333 h at 10.00 -> stored TotalBeforeVat 3.33. Rounding the *quantity* to 2 decimals
        // (like a money amount) would emit 0.33, so InvoicedQuantity * PriceAmount = 3.30 would
        // silently drift from the stated LineExtensionAmount (PEPPOL-EN16931-R120).
        var invoice = BuildInvoice(CzIssuer(), SkClient(), "EUR", [Item(1, "Fractional", 0.3333m, 10m, 21m)]);

        var document = UblMapper.Map(invoice);

        var cac = UblMapper.CacNs;
        var cbc = UblMapper.CbcNs;
        document.Root!.Element(cac + "InvoiceLine")!.Element(cbc + "InvoicedQuantity")!.Value.ShouldBe("0.3333");

        UblTestValidator.ValidateXsd(document).ShouldBeEmpty();
        UblTestValidator.ValidateSchematron(document).ShouldBeEmpty();
    }

    [Fact]
    public void Map_VatPayer_AllLinesOutOfScope_OmitsPartyTaxScheme()
    {
        // BR-O-02: a document whose lines are entirely category O must not carry a seller/buyer
        // VAT identifier, even though the issuer IS a VAT payer (only the *lines* say "O" here).
        var item = Item(1, "Non-business recharge", 1, 500m, 0m);
        item.VatRegime = EVatRegime.OutOfScope;
        var invoice = BuildInvoice(CzIssuer(), SkClient(), "EUR", [item]);

        var document = UblMapper.Map(invoice);

        document.Root!.Descendants(UblMapper.CacNs + "PartyTaxScheme").ShouldBeEmpty();

        UblTestValidator.ValidateXsd(document).ShouldBeEmpty();
        UblTestValidator.ValidateSchematron(document).ShouldBeEmpty();
    }

    [Fact]
    public void Map_CreditNote_MixedSignLines_BothLinesBecomePositive()
    {
        // Lines -100 and +20 net to a positive document total (+20 * 1.21 etc.) -- a flip
        // decided from the document-level sign would have left the -100 line negative in the
        // exported XML. Every line must be taken by absolute value independently.
        var invoice = BuildInvoice(CzIssuer(), SkClient(), "EUR",
            [Item(1, "Returned item", 1, -100m, 21m), Item(2, "Extra charge", 1, 20m, 21m)]);
        invoice.DocumentType = EDocumentType.CreditNote;
        invoice.DocumentNumber = "CN2026002";

        var document = UblMapper.Map(invoice, precedingDocumentNumbers: ["INV2026001"]);

        var cac = UblMapper.CacNs;
        var cbc = UblMapper.CbcNs;
        foreach (var line in document.Root!.Elements(cac + "CreditNoteLine"))
        {
            decimal.Parse(line.Element(cbc + "CreditedQuantity")!.Value, CultureInfo.InvariantCulture).ShouldBeGreaterThan(0m);
            var priceAmount = line.Element(cac + "Price")!.Element(cbc + "PriceAmount")!.Value;
            decimal.Parse(priceAmount, CultureInfo.InvariantCulture).ShouldBeGreaterThan(0m);
        }

        UblTestValidator.ValidateXsd(document).ShouldBeEmpty();
        UblTestValidator.ValidateSchematron(document).ShouldBeEmpty();
    }

    [Fact]
    public void Map_BlankIban_FallsBackToBankAccountNumber()
    {
        // Empty string (not null) IBAN must still fall back to BankAccountNumber, not emit a
        // blank PayeeFinancialAccount/ID (BR-61).
        var invoice = BuildInvoice(CzIssuer(), SkClient(), "EUR", [Item(1, "Service", 1, 100m, 21m)]);
        invoice.IBAN = "";
        invoice.BankAccountNumber = "1234567890/0100";

        var document = UblMapper.Map(invoice);

        var cac = UblMapper.CacNs;
        var cbc = UblMapper.CbcNs;
        var accountId = document.Root!.Element(cac + "PaymentMeans")!
            .Element(cac + "PayeeFinancialAccount")!.Element(cbc + "ID")!.Value;
        accountId.ShouldBe("1234567890/0100");

        UblTestValidator.ValidateXsd(document).ShouldBeEmpty();
        UblTestValidator.ValidateSchematron(document).ShouldBeEmpty();
    }

    [Fact]
    public void Map_SkVatPayer_BareTaxNumber_GetsCountryPrefixInPartyTaxScheme()
    {
        // BR-CO-09: the VAT ID (BT-31) must carry the ISO country prefix. A bare 10-digit DIC
        // (the format a non-payer's Peppol endpoint derivation accepts) must not leak into the
        // VAT ID unprefixed for a VAT-payer whose TaxNumber was entered without "SK".
        var issuer = SkIssuer();
        issuer.TaxNumber = "2020123456"; // no "SK" prefix, but IsVatPayer = true
        var invoice = BuildInvoice(issuer, CzIssuer(), "EUR", [Item(1, "Service", 1, 100m, 21m)]);

        var document = UblMapper.Map(invoice);

        var cac = UblMapper.CacNs;
        var cbc = UblMapper.CbcNs;
        var supplierVatId = document.Root!.Element(cac + "AccountingSupplierParty")!.Element(cac + "Party")!
            .Element(cac + "PartyTaxScheme")!.Element(cbc + "CompanyID")!.Value;
        supplierVatId.ShouldBe("SK2020123456");

        UblTestValidator.ValidateXsd(document).ShouldBeEmpty();
        UblTestValidator.ValidateSchematron(document).ShouldBeEmpty();
    }

    // --------------------------------------------------------------------------
    // Test data builders
    // --------------------------------------------------------------------------

    private static Invoice BuildInvoice(Client issuer, Client client, string currencyCode, List<InvoiceItem> items)
    {
        var totalBeforeVat = items.Where(i => !i.IsTextRow).Sum(i => i.Quantity * i.UnitPrice);
        var totalVat = items.Where(i => !i.IsTextRow).Sum(i => Math.Round(i.Quantity * i.UnitPrice * i.VatRatePercentage / 100m, 2));

        foreach (var item in items.Where(i => !i.IsTextRow))
        {
            item.TotalBeforeVat = item.Quantity * item.UnitPrice;
            item.VatAmount = Math.Round(item.TotalBeforeVat * item.VatRatePercentage / 100m, 2);
            item.TotalWithVat = item.TotalBeforeVat + item.VatAmount;
        }

        return new Invoice
        {
            Id = 1,
            DocumentType = EDocumentType.Invoice,
            Status = EInvoiceStatus.Completed,
            DocumentNumber = "INV2026001",
            IssueDate = new DateTime(2026, 3, 1),
            DueDate = new DateTime(2026, 3, 15),
            TaxableSupplyDate = new DateTime(2026, 3, 1),
            Issuer = issuer,
            Client = client,
            Currency = new Currency { Id = 1, Code = currencyCode },
            PaymentMethod = EPaymentMethod.BankTransfer,
            IBAN = "SK8975000000000012345671",
            SWIFT = "CEKOSKBX",
            VariableSymbol = "2026001",
            Notes = null,
            TotalBeforeVat = totalBeforeVat,
            TotalVat = totalVat,
            TotalWithVat = totalBeforeVat + totalVat,
            InvoiceItem = items
        };
    }

    private static InvoiceItem Item(int orderIndex, string description, decimal quantity, decimal unitPrice, decimal vatRate)
        => new()
        {
            OrderIndex = orderIndex,
            Description = description,
            Quantity = quantity,
            Unit = "ks",
            UnitPrice = unitPrice,
            VatRatePercentage = vatRate,
            VatRegime = EVatRegime.Standard,
            ProductCode = "SVC-001"
        };

    private static Client CzIssuer() => new()
    {
        Id = 1, CompanyName = "Fakvio s.r.o.",
        RegistrationNumber = "11223344", TaxNumber = "CZ11223344",
        IsVatPayer = true, IsIssuer = true, IsActive = true,
        Address = [new Address { Street = "Narodni 1", City = "Praha", PostalCode = "11000", Country = "CZ", IsPrimary = true }]
    };

    private static Client SkIssuer() => new()
    {
        Id = 1, CompanyName = "Fakvio s.r.o.",
        RegistrationNumber = "11223344", TaxNumber = "SK2020123456",
        IsVatPayer = true, IsIssuer = true, IsActive = true,
        Address = [new Address { Street = "Hlavna 1", City = "Bratislava", PostalCode = "81101", Country = "SK", IsPrimary = true }]
    };

    private static Client SkClient() => new()
    {
        Id = 2, CompanyName = "Odberatel s.r.o.",
        RegistrationNumber = "55667788", TaxNumber = "SK2020123456",
        IsVatPayer = true, IsIssuer = false, IsActive = true,
        Address = [new Address { Street = "Hlavna 2", City = "Bratislava", PostalCode = "81102", Country = "SK", IsPrimary = true }]
    };
}

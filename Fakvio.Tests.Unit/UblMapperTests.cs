using Fakvio.Domain.Entities;
using Fakvio.Domain.Enums;
using Fakvio.Infrastructure.Service.Ubl;
using Fakvio.Tests.Unit.Ubl;
using Shouldly;

namespace Fakvio.Tests.Unit;

/// <summary>
/// Unit tests for <c>UblMapper</c> (ADR 0002, F1.3). Mirrors <see cref="IsdocExportServiceTests"/>
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

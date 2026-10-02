using System.Xml.Linq;
using System.Xml.Schema;
using Fakvio.Domain.Entities;
using Fakvio.Domain.Enums;
using Fakvio.Infrastructure.Service;
using Fakvio.Infrastructure.Service.Isdoc;
using Shouldly;

namespace Fakvio.Tests.Unit;

/// <summary>
/// ISDOC 6.0.2 mapping for ReverseCharge (PDP, §92a-92e ZDPH) items: each line must carry
/// ClassifiedTaxCategory/LocalReverseCharge (LocalReverseChargeCode + LocalReverseChargeQuantity),
/// and the header TaxTotal must report the PDP base in its own TaxSubTotal, flagged with
/// LocalReverseChargeFlag, never merged with a Standard-regime TaxSubTotal at the same rate.
/// Element names and structure verified against the embedded isdoc-6.0.2.xsd
/// (ClassifiedTaxCategoryType.LocalReverseCharge, LocalReverseChargeType, TaxCategoryType.LocalReverseChargeFlag).
/// </summary>
public class IsdocReverseChargeExportTests
{
    private static readonly XNamespace Ns = IsdocMapper.Ns;

    private static Invoice BuildInvoiceWithReverseCharge()
    {
        return new Invoice
        {
            Id = 200,
            DocumentType = EDocumentType.Invoice,
            Status = EInvoiceStatus.Completed,
            DocumentNumber = "INV2026200",
            IssueDate = new DateTime(2026, 5, 1),
            DueDate = new DateTime(2026, 5, 15),
            TaxableSupplyDate = new DateTime(2026, 5, 1),
            TotalBeforeVat = 2000, TotalVat = 210, TotalWithVat = 2210,
            PaymentMethod = EPaymentMethod.BankTransfer,
            BankAccountNumber = "1234567890/0100",
            VariableSymbol = "2026200",
            Currency = new Currency { Code = "CZK" },
            Issuer = new Client
            {
                RegistrationNumber = "11223344", TaxNumber = "CZ11223344",
                CompanyName = "Fakvio s.r.o.", IsVatPayer = true,
                Address = new List<Address>
                {
                    new() { Street = "Narodni 1", City = "Praha", PostalCode = "11000", Country = "CZ",
                        IsPrimary = true, AddressType = EAddressType.Billing }
                },
                Contact = new List<Contact>()
            },
            Client = new Client
            {
                RegistrationNumber = "55667788", TaxNumber = "CZ55667788",
                CompanyName = "Odberatel a.s.", IsVatPayer = true,
                Address = new List<Address>
                {
                    new() { Street = "Masarykova 2", City = "Brno", PostalCode = "60200", Country = "CZ",
                        IsPrimary = true, AddressType = EAddressType.Billing }
                },
                Contact = new List<Contact>()
            },
            InvoiceItem = new List<InvoiceItem>
            {
                new()
                {
                    OrderIndex = 1, Description = "Poradenstvi",
                    Quantity = 10, Unit = "hod", UnitPrice = 100,
                    VatRatePercentage = 21, TotalBeforeVat = 1000, VatAmount = 210, TotalWithVat = 1210,
                    VatRegime = EVatRegime.Standard
                },
                new()
                {
                    OrderIndex = 2, Description = "Stavebni prace",
                    Quantity = 5, Unit = "hod", UnitPrice = 200,
                    VatRatePercentage = 21, TotalBeforeVat = 1000, VatAmount = 0, TotalWithVat = 1000,
                    InformationalVatAmount = 210,
                    VatRegime = EVatRegime.ReverseCharge,
                    ReverseChargeCode = new ReverseChargeCode
                    {
                        Code = "4", NameCs = "Stavebni nebo montazni prace", ParagraphRef = "§92d"
                    }
                }
            }
        };
    }

    [Fact]
    public void Map_ReverseChargeLine_HasLocalReverseChargeBlockWithCode()
    {
        var doc = IsdocMapper.Map(BuildInvoiceWithReverseCharge());

        var lines = doc.Descendants(Ns + "InvoiceLine").ToList();
        lines.Count.ShouldBe(2);

        var rcLine = lines[1]; // second item is the ReverseCharge one
        var localReverseCharge = rcLine
            .Element(Ns + "ClassifiedTaxCategory")!
            .Element(Ns + "LocalReverseCharge");

        localReverseCharge.ShouldNotBeNull();
        localReverseCharge!.Element(Ns + "LocalReverseChargeCode")!.Value.ShouldBe("4");
        localReverseCharge.Element(Ns + "LocalReverseChargeQuantity")!.Value.ShouldBe("5.00");

        // The LineExtensionTaxAmount must be 0 — the item bills no VAT.
        rcLine.Element(Ns + "LineExtensionTaxAmount")!.Value.ShouldBe("0.00");
        // No VAT is billed, so the tax-inclusive unit price equals the net one.
        rcLine.Element(Ns + "UnitPriceTaxInclusive")!.Value.ShouldBe(rcLine.Element(Ns + "UnitPrice")!.Value);
    }

    [Fact]
    public void Map_StandardLine_HasNoLocalReverseChargeBlock()
    {
        var doc = IsdocMapper.Map(BuildInvoiceWithReverseCharge());

        var standardLine = doc.Descendants(Ns + "InvoiceLine").First();
        standardLine
            .Element(Ns + "ClassifiedTaxCategory")!
            .Element(Ns + "LocalReverseCharge")
            .ShouldBeNull();
    }

    [Fact]
    public void Map_HeaderTaxTotal_SplitsReverseChargeIntoOwnFlaggedSubTotal()
    {
        var doc = IsdocMapper.Map(BuildInvoiceWithReverseCharge());

        var subTotals = doc.Descendants(Ns + "TaxSubTotal").ToList();
        // Both items share VatRatePercentage=21, but must NOT be merged: Standard vs ReverseCharge.
        subTotals.Count.ShouldBe(2);

        var rcSubTotal = subTotals.Single(st =>
            st.Element(Ns + "TaxCategory")!.Element(Ns + "LocalReverseChargeFlag") != null);
        rcSubTotal.Element(Ns + "TaxableAmount")!.Value.ShouldBe("1000.00");
        rcSubTotal.Element(Ns + "TaxAmount")!.Value.ShouldBe("0.00");
        rcSubTotal.Element(Ns + "TaxCategory")!.Element(Ns + "LocalReverseChargeFlag")!.Value.ShouldBe("true");

        var standardSubTotal = subTotals.Single(st =>
            st.Element(Ns + "TaxCategory")!.Element(Ns + "LocalReverseChargeFlag") == null);
        standardSubTotal.Element(Ns + "TaxableAmount")!.Value.ShouldBe("1000.00");
        standardSubTotal.Element(Ns + "TaxAmount")!.Value.ShouldBe("210.00");
    }

    [Fact]
    public void Map_ReverseChargeInvoice_ValidatesAgainstXsd()
    {
        var doc = IsdocMapper.Map(BuildInvoiceWithReverseCharge());

        var schemas = IsdocExportService.LoadSchemaSet();
        var errors = new List<string>();
        doc.Validate(schemas, (_, e) => errors.Add(e.Message));

        errors.ShouldBeEmpty($"XSD validation errors:\n{string.Join("\n", errors)}");
    }
}

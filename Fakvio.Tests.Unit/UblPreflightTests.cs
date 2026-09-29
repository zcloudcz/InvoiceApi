using Fakvio.Contracts.Dto.Readiness;
using Fakvio.Domain.Entities;
using Fakvio.Domain.Enums;
using Fakvio.Infrastructure.Service.Ubl;
using Shouldly;

namespace Fakvio.Tests.Unit;

/// <summary>
/// Unit tests for <c>UblPreflight</c> (ADR 0002, F1.5) — the per-document readiness gate the
/// UBL export runs before mapping. Every rule here maps to one <c>EINVOICE_*</c> code in
/// <see cref="ReadinessCodes"/>.
/// </summary>
public class UblPreflightTests
{
    [Fact]
    public void ReadyInvoice_HasNoIssues()
    {
        var invoice = ValidInvoice();

        UblPreflight.Check(invoice).ShouldBeEmpty();
    }

    [Fact]
    public void Draft_IsBlocking()
    {
        var invoice = ValidInvoice();
        invoice.Status = EInvoiceStatus.Draft;

        UblPreflight.Check(invoice).ShouldContain(i => i.Code == ReadinessCodes.EinvoiceDraft);
    }

    [Fact]
    public void Proforma_IsBlocking()
    {
        var invoice = ValidInvoice();
        invoice.DocumentType = EDocumentType.Proforma;

        UblPreflight.Check(invoice).ShouldContain(i => i.Code == ReadinessCodes.EinvoiceProformaNotSupported);
    }

    [Fact]
    public void UnsupportedOrInvalidDocumentType_IsBlocking_InsteadOfCrashingTheMapper()
    {
        // Codex review round 1: the original check only rejected Proforma explicitly, so any
        // other value UblMapper cannot map (a future document type, or a corrupted/out-of-range
        // enum value) would sail through preflight and hit UblMapper's own
        // InvalidOperationException -- a 500, not a structured 400. The allow-list must reject
        // anything that is not one of the three types UblMapper actually knows how to map.
        var invoice = ValidInvoice();
        invoice.DocumentType = (EDocumentType)999;

        UblPreflight.Check(invoice).ShouldContain(i => i.Code == ReadinessCodes.EinvoiceProformaNotSupported);
    }

    [Theory]
    [InlineData(EDocumentType.Invoice)]
    [InlineData(EDocumentType.TaxReceiptForAdvance)]
    [InlineData(EDocumentType.CreditNote)]
    public void SupportedDocumentTypes_AreNotBlockedByTheTypeCheck(EDocumentType type)
    {
        var invoice = ValidInvoice();
        invoice.DocumentType = type;

        UblPreflight.Check(invoice).ShouldNotContain(i => i.Code == ReadinessCodes.EinvoiceProformaNotSupported);
    }

    [Fact]
    public void MissingClient_IsBlocking()
    {
        var invoice = ValidInvoice();
        invoice.Client = null;

        UblPreflight.Check(invoice).ShouldContain(i => i.Code == ReadinessCodes.EinvoiceBuyerMissing);
    }

    [Fact]
    public void SellerWithoutTaxNumber_HasNoEndpointId_IsBlocking()
    {
        var invoice = ValidInvoice();
        invoice.Issuer!.TaxNumber = null;

        var issues = UblPreflight.Check(invoice);
        issues.ShouldContain(i => i.Code == ReadinessCodes.EinvoiceSellerEndpointMissing);
    }

    [Fact]
    public void BuyerWithUnresolvableCountry_HasNoEndpointId_IsBlocking()
    {
        var invoice = ValidInvoice();
        invoice.Client!.Address!.First().Country = "Narnia";

        var issues = UblPreflight.Check(invoice);
        issues.ShouldContain(i => i.Code == ReadinessCodes.EinvoiceBuyerEndpointMissing);
        // Narnia also fails EinvoiceAddressIncomplete (unresolvable ISO country) — both fire.
        issues.ShouldContain(i => i.Code == ReadinessCodes.EinvoiceAddressIncomplete);
    }

    [Fact]
    public void IncompleteAddress_ReportsMissingFields()
    {
        var invoice = ValidInvoice();
        invoice.Issuer!.Address!.First().Street = "";

        var issue = UblPreflight.Check(invoice).Single(i => i.Code == ReadinessCodes.EinvoiceAddressIncomplete);
        issue.MissingFields.ShouldContain(nameof(Address.Street));
        issue.FixRoute.ShouldBe("/my-company");
    }

    [Fact]
    public void BankTransferWithoutAccount_IsBlocking()
    {
        var invoice = ValidInvoice();
        invoice.PaymentMethod = EPaymentMethod.BankTransfer;
        invoice.IBAN = null;
        invoice.BankAccountNumber = null;

        UblPreflight.Check(invoice).ShouldContain(i => i.Code == ReadinessCodes.EinvoicePaymentAccountMissing);
    }

    [Fact]
    public void CashPayment_NeedsNoAccount()
    {
        var invoice = ValidInvoice();
        invoice.PaymentMethod = EPaymentMethod.Cash;
        invoice.IBAN = null;
        invoice.BankAccountNumber = null;

        UblPreflight.Check(invoice).ShouldNotContain(i => i.Code == ReadinessCodes.EinvoicePaymentAccountMissing);
    }

    [Fact]
    public void MixedOutOfScopeAndStandardLines_IsBlocking()
    {
        var invoice = ValidInvoice();
        invoice.InvoiceItem =
        [
            Item(1, 100m, 21m, EVatRegime.Standard),
            Item(2, 50m, 0m, EVatRegime.OutOfScope)
        ];

        UblPreflight.Check(invoice).ShouldContain(i => i.Code == ReadinessCodes.EinvoiceOutOfScopeMixed);
    }

    [Fact]
    public void OnlyOutOfScopeLines_IsNotMixed()
    {
        var invoice = ValidInvoice();
        invoice.InvoiceItem = [Item(1, 100m, 0m, EVatRegime.OutOfScope)];

        UblPreflight.Check(invoice).ShouldNotContain(i => i.Code == ReadinessCodes.EinvoiceOutOfScopeMixed);
    }

    [Fact]
    public void ReverseCharge_WithoutBuyerVatId_IsBlocking()
    {
        var invoice = ValidInvoice();
        invoice.InvoiceItem = [Item(1, 100m, 0m, EVatRegime.ReverseCharge)];
        invoice.Client!.IsVatPayer = false;
        invoice.Client!.TaxNumber = null;

        UblPreflight.Check(invoice).ShouldContain(i => i.Code == ReadinessCodes.EinvoiceReverseChargeVatIdMissing);
    }

    [Fact]
    public void ReverseCharge_WithBothVatIds_IsNotBlocking()
    {
        var invoice = ValidInvoice();
        invoice.InvoiceItem = [Item(1, 100m, 0m, EVatRegime.ReverseCharge)];

        UblPreflight.Check(invoice).ShouldNotContain(i => i.Code == ReadinessCodes.EinvoiceReverseChargeVatIdMissing);
    }

    [Fact]
    public void SkIssuer_NonEurCurrency_IsBlocking()
    {
        var invoice = ValidInvoice();
        invoice.Issuer!.Address = [new Address { Street = "Hlavna 1", City = "Bratislava", PostalCode = "81101", Country = "SK", IsPrimary = true }];
        invoice.Issuer!.TaxNumber = "SK2020123456";
        invoice.Currency = new Currency { Id = 1, Code = "CZK" };

        UblPreflight.Check(invoice).ShouldContain(i => i.Code == ReadinessCodes.EinvoiceSkNonEurCurrency);
    }

    [Fact]
    public void NoLines_IsBlocking()
    {
        var invoice = ValidInvoice();
        invoice.InvoiceItem = [];

        UblPreflight.Check(invoice).ShouldContain(i => i.Code == ReadinessCodes.EinvoiceNoLines);
    }

    [Fact]
    public void TextOnlyRows_CountAsNoLines()
    {
        var invoice = ValidInvoice();
        invoice.InvoiceItem = [new InvoiceItem { IsTextRow = true, Description = "Note only" }];

        UblPreflight.Check(invoice).ShouldContain(i => i.Code == ReadinessCodes.EinvoiceNoLines);
    }

    // --------------------------------------------------------------------------
    // Builders
    // --------------------------------------------------------------------------

    private static Invoice ValidInvoice() => new()
    {
        Id = 1,
        DocumentType = EDocumentType.Invoice,
        Status = EInvoiceStatus.Completed,
        DocumentNumber = "INV2026001",
        PaymentMethod = EPaymentMethod.BankTransfer,
        IBAN = "SK8975000000000012345671",
        Currency = new Currency { Id = 1, Code = "EUR" },
        Issuer = new Client
        {
            Id = 1, CompanyName = "Fakvio s.r.o.",
            RegistrationNumber = "11223344", TaxNumber = "CZ11223344",
            IsVatPayer = true, IsIssuer = true,
            Address = [new Address { Street = "Narodni 1", City = "Praha", PostalCode = "11000", Country = "CZ", IsPrimary = true }]
        },
        Client = new Client
        {
            Id = 2, CompanyName = "Odberatel s.r.o.",
            RegistrationNumber = "55667788", TaxNumber = "SK2020123456",
            IsVatPayer = true,
            Address = [new Address { Street = "Hlavna 2", City = "Bratislava", PostalCode = "81102", Country = "SK", IsPrimary = true }]
        },
        InvoiceItem = [Item(1, 100m, 21m, EVatRegime.Standard)]
    };

    private static InvoiceItem Item(int orderIndex, decimal unitPrice, decimal vatRate, EVatRegime regime) => new()
    {
        OrderIndex = orderIndex,
        Description = "Line",
        Quantity = 1,
        Unit = "ks",
        UnitPrice = unitPrice,
        VatRatePercentage = vatRate,
        VatRegime = regime,
        TotalBeforeVat = unitPrice
    };
}

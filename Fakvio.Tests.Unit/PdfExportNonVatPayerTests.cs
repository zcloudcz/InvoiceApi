using Fakvio.Domain.Entities;
using Fakvio.Domain.Enums;
using Fakvio.Infrastructure.Data;
using Fakvio.Infrastructure.Service;
using Shouldly;

namespace Fakvio.Tests.Unit;

/// <summary>
/// A non-VAT payer (neplátce DPH) must not issue a PDF that talks about VAT: no VAT rate
/// column, no VAT recapitulation, "CELKEM" instead of "CELKEM BEZ DPH", and a line saying
/// the supplier is not a VAT payer. A VAT payer's PDF stays exactly as before.
/// Rendered against the built-in templates, which every tenant's seeded templates copy.
/// </summary>
public class PdfExportNonVatPayerTests
{
    private static Invoice BuildInvoice(bool issuerIsVatPayer, string clientLanguage = "cs") => new()
    {
        Id = 1,
        DocumentType = EDocumentType.Invoice,
        DocumentNumber = "2026001",
        Issuer = new Client { CompanyName = "Moje OSVČ", IsVatPayer = issuerIsVatPayer },
        Client = new Client { CompanyName = "Zákazník", Language = clientLanguage },
        Currency = new Currency { Code = "CZK", Symbol = "Kč" },
        TotalBeforeVat = 1500,
        TotalWithVat = 1500,
        InvoiceItem = new List<InvoiceItem>
        {
            new() { OrderIndex = 1, Description = "Konzultace", Unit = "h", Quantity = 10, UnitPrice = 150, TotalBeforeVat = 1500 }
        }
    };

    public static TheoryData<string> Templates => new()
    {
        DefaultSeedData.GetDefaultInvoicePdfTemplate(),
        DefaultSeedData.GetDefaultCreditNotePdfTemplate(),
        DefaultSeedData.GetDefaultAdvanceInvoicePdfTemplate()
    };

    [Theory]
    [MemberData(nameof(Templates))]
    public void NonVatPayer_PdfHasNoVatColumnNoRecapAndStatesNonPayer(string template)
    {
        var html = PdfExportService.ReplacePlaceholders(template, BuildInvoice(issuerIsVatPayer: false));

        html.ShouldNotContain(">DPH</th>");
        html.ShouldNotContain("CELKEM BEZ DPH");
        html.ShouldNotContain(">SAZBA</th>");
        html.ShouldNotContain("0 %</td>");
        html.ShouldNotContain(">DAŇOVÝ DOKLAD<");
        html.ShouldNotContain("DATUM ZDAN. PLNĚNÍ");
        html.ShouldContain("Dodavatel není plátcem DPH.");
    }

    [Fact]
    public void NonVatPayer_EnglishClient_NoteIsInEnglish()
    {
        var html = PdfExportService.ReplacePlaceholders(
            DefaultSeedData.GetDefaultInvoicePdfTemplate(), BuildInvoice(issuerIsVatPayer: false, clientLanguage: "en"));

        html.ShouldContain("The supplier is not a VAT payer.");
    }

    [Fact]
    public void VatPayer_PdfKeepsVatColumnAndRecap()
    {
        var html = PdfExportService.ReplacePlaceholders(
            DefaultSeedData.GetDefaultInvoicePdfTemplate(), BuildInvoice(issuerIsVatPayer: true));

        html.ShouldContain(">DPH</th>");
        html.ShouldContain("CELKEM BEZ DPH");
        html.ShouldContain(">SAZBA</th>");
        html.ShouldContain(">DAŇOVÝ DOKLAD<");
        html.ShouldContain("DATUM ZDAN. PLNĚNÍ");
        html.ShouldNotContain("není plátcem DPH");
    }
}

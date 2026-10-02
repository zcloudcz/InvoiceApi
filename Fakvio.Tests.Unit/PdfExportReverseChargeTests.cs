using Fakvio.Domain.Entities;
using Fakvio.Domain.Enums;
using Fakvio.Infrastructure.Data;
using Fakvio.Infrastructure.Service;
using Shouldly;

namespace Fakvio.Tests.Unit;

/// <summary>
/// An invoice with at least one ReverseCharge (PDP, §92a ZDPH) item must render a mandatory
/// note on the PDF ("daň odvede zákazník" + legal reference + the reverse charge code used),
/// and must not lump the PDP base into the regular VAT recapitulation at its nominal rate.
/// Rendered directly in code against the built-in templates (same approach as the non-VAT-payer
/// note in PdfExportService.StripVatFromTemplate) — every tenant's seeded template copy picks
/// this up without needing a per-template placeholder edit.
/// </summary>
public class PdfExportReverseChargeTests
{
    private static Invoice BuildInvoice(string clientLanguage = "cs") => new()
    {
        Id = 1,
        DocumentType = EDocumentType.Invoice,
        DocumentNumber = "2026001",
        Issuer = new Client { CompanyName = "Moje s.r.o.", IsVatPayer = true },
        Client = new Client { CompanyName = "Zákazník", Language = clientLanguage },
        Currency = new Currency { Code = "CZK", Symbol = "Kč" },
        TotalBeforeVat = 2000,
        TotalVat = 210,
        TotalWithVat = 2210,
        InvoiceItem = new List<InvoiceItem>
        {
            new()
            {
                OrderIndex = 1, Description = "Konzultace", Unit = "h", Quantity = 10, UnitPrice = 100,
                VatRatePercentage = 21, TotalBeforeVat = 1000, VatAmount = 210, TotalWithVat = 1210,
                VatRegime = EVatRegime.Standard
            },
            new()
            {
                OrderIndex = 2, Description = "Montážní práce", Unit = "h", Quantity = 5, UnitPrice = 200,
                VatRatePercentage = 21, TotalBeforeVat = 1000, VatAmount = 0, TotalWithVat = 1000,
                InformationalVatAmount = 210,
                VatRegime = EVatRegime.ReverseCharge,
                ReverseChargeCode = new ReverseChargeCode
                {
                    Code = "11", NameCs = "Stavební nebo montážní práce",
                    NameEn = "Construction or assembly work", ParagraphRef = "§92d"
                }
            }
        }
    };

    [Fact]
    public void ReverseChargeItem_PdfContainsNoteAndCode()
    {
        var html = PdfExportService.ReplacePlaceholders(
            DefaultSeedData.GetDefaultInvoicePdfTemplate(), BuildInvoice());

        html.ShouldContain("§92a zákona č. 235/2004 Sb.");
        html.ShouldContain("daň odvede zákazník");
        html.ShouldContain("kód předmětu plnění 11");
        html.ShouldContain("§92d");
    }

    [Fact]
    public void ReverseChargeItem_EnglishClient_NoteIsInEnglish()
    {
        var html = PdfExportService.ReplacePlaceholders(
            DefaultSeedData.GetDefaultInvoicePdfTemplate(), BuildInvoice(clientLanguage: "en"));

        html.ShouldContain("§92a of Act No. 235/2004 Coll.");
        html.ShouldContain("self-assessed by the customer");
        html.ShouldContain("supply code 11");
    }

    [Fact]
    public void ReverseChargeItem_LineRowMarkedWithAsterisk()
    {
        var html = PdfExportService.ReplacePlaceholders(
            DefaultSeedData.GetDefaultInvoicePdfTemplate(), BuildInvoice());

        html.ShouldContain("21 % *");
    }

    [Fact]
    public void ReverseChargeItem_RecapHasSeparateRowForPdp()
    {
        var html = PdfExportService.ReplacePlaceholders(
            DefaultSeedData.GetDefaultInvoicePdfTemplate(), BuildInvoice());

        // The PDP recap row is marked distinctly and states who pays, instead of
        // silently netting its 0 VAT into the real 210 CZK from the Standard item at the same rate.
        html.ShouldContain("21 % (PDP)");
        html.ShouldContain("daň odvede zákazník");
    }

    [Fact]
    public void StandardOnlyInvoice_NoReverseChargeNote()
    {
        var invoice = BuildInvoice();
        invoice.InvoiceItem = invoice.InvoiceItem.Where(i => i.VatRegime == EVatRegime.Standard).ToList();

        var html = PdfExportService.ReplacePlaceholders(
            DefaultSeedData.GetDefaultInvoicePdfTemplate(), invoice);

        html.ShouldNotContain("§92a");
        html.ShouldNotContain("daň odvede zákazník");
    }
}

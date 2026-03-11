using Fakvio.Application.QrPayment;
using Fakvio.Infrastructure.Service;
using Shouldly;

namespace Fakvio.Tests.Unit;

/// <summary>
/// Unit tests for <see cref="InvoiceTextExtractorService"/> — regex-based invoice text extraction.
/// Each test provides a realistic text snippet and verifies the correct field is extracted.
///
/// The test texts simulate what PdfTextExtractorService produces from real Czech invoices.
/// </summary>
public class InvoiceTextExtractorServiceTests
{
    private readonly InvoiceTextExtractorService _service = new();

    // ─── Document number extraction ──────────────────────────────────────

    [Theory]
    [InlineData("Faktura č. FV2026001", "FV2026001")]
    [InlineData("Faktura č.: 20260042", "20260042")]
    [InlineData("Číslo faktury: INV-2026/001", "INV-2026/001")]
    [InlineData("Invoice No. ABC123", "ABC123")]
    public void Extract_DocumentNumber_VariousFormats(string text, string expected)
    {
        var result = _service.Extract(text);
        result.DocumentNumber.ShouldBe(expected);
    }

    // ─── Date extraction ─────────────────────────────────────────────────

    [Fact]
    public void Extract_IssueDate_CzechFormat()
    {
        var text = "Datum vystavení: 01.03.2026";
        var result = _service.Extract(text);
        result.IssueDate.ShouldBe(new DateTime(2026, 3, 1));
    }

    [Fact]
    public void Extract_IssueDate_IsoFormat()
    {
        var text = "Date of issue: 2026-03-01";
        var result = _service.Extract(text);
        result.IssueDate.ShouldBe(new DateTime(2026, 3, 1));
    }

    [Fact]
    public void Extract_DueDate_WithSpaces()
    {
        var text = "Datum splatnosti: 15. 03. 2026";
        var result = _service.Extract(text);
        result.DueDate.ShouldBe(new DateTime(2026, 3, 15));
    }

    [Fact]
    public void Extract_TaxableSupplyDate_Duzp()
    {
        var text = "DUZP: 01.03.2026";
        var result = _service.Extract(text);
        result.TaxableSupplyDate.ShouldBe(new DateTime(2026, 3, 1));
    }

    // ─── Amount extraction ───────────────────────────────────────────────

    [Theory]
    [InlineData("Celkem k úhradě: 12 100,00 Kč", 12100.00)]
    [InlineData("Celkem: 5850.00 CZK", 5850.00)]
    [InlineData("Total amount: 1234.56", 1234.56)]
    public void Extract_TotalAmount_VariousFormats(string text, decimal expected)
    {
        var result = _service.Extract(text);
        result.TotalAmount.ShouldBe(expected);
    }

    [Fact]
    public void Extract_TotalBeforeVat()
    {
        var text = "Základ daně: 10 000,00";
        var result = _service.Extract(text);
        result.TotalBeforeVat.ShouldBe(10000.00m);
    }

    [Fact]
    public void Extract_TotalVat()
    {
        var text = "DPH celkem: 2 100,00";
        var result = _service.Extract(text);
        result.TotalVat.ShouldBe(2100.00m);
    }

    // ─── Payment details extraction ──────────────────────────────────────

    [Theory]
    [InlineData("Variabilní symbol: 2026001", "2026001")]
    [InlineData("VS: 123456", "123456")]
    public void Extract_VariableSymbol(string text, string expected)
    {
        var result = _service.Extract(text);
        result.VariableSymbol.ShouldBe(expected);
    }

    [Fact]
    public void Extract_IBAN()
    {
        var text = "IBAN: CZ58 5500 0000 0012 6509 8001";
        var result = _service.Extract(text);
        result.IBAN.ShouldBe("CZ5855000000001265098001");
    }

    [Theory]
    [InlineData("Číslo účtu: 1265098001/5500", "1265098001/5500")]
    [InlineData("Bankovní účet: 19-1234567890/0100", "19-1234567890/0100")]
    public void Extract_CzechBankAccount(string text, string expected)
    {
        var result = _service.Extract(text);
        result.BankAccountNumber.ShouldBe(expected);
    }

    // ─── Identification numbers ──────────────────────────────────────────

    [Fact]
    public void Extract_SingleIco_AssignedToIssuer()
    {
        var text = "IČO: 12345678";
        var result = _service.Extract(text);
        result.IssuerRegistrationNumber.ShouldBe("12345678");
        result.RecipientRegistrationNumber.ShouldBeNull();
    }

    [Fact]
    public void Extract_TwoIcos_FirstIsIssuerSecondIsRecipient()
    {
        // Typical invoice layout: issuer at top, recipient below
        var text = """
            Dodavatel
            IČ: 12345678

            Odběratel
            IČ: 87654321
            """;

        var result = _service.Extract(text);
        result.IssuerRegistrationNumber.ShouldBe("12345678");
        result.RecipientRegistrationNumber.ShouldBe("87654321");
    }

    [Fact]
    public void Extract_TaxNumbers()
    {
        var text = """
            DIČ: CZ12345678
            DIČ: CZ87654321
            """;

        var result = _service.Extract(text);
        result.IssuerTaxNumber.ShouldBe("CZ12345678");
        result.RecipientTaxNumber.ShouldBe("CZ87654321");
    }

    // ─── Currency detection ──────────────────────────────────────────────

    [Theory]
    [InlineData("Celkem: 1000 Kč", "CZK")]
    [InlineData("Total: 500 EUR", "EUR")]
    [InlineData("Amount: 200 USD", "USD")]
    public void Extract_Currency(string text, string expected)
    {
        var result = _service.Extract(text);
        result.Currency.ShouldBe(expected);
    }

    // ─── Combined realistic invoice text ─────────────────────────────────

    [Fact]
    public void Extract_FullCzechInvoice_ExtractsMultipleFields()
    {
        var text = """
            --- Page 1 ---
            Fakvio s.r.o.
            IČ: 12345678
            DIČ: CZ12345678

            Faktura č. FV2026042

            Odběratel:
            Klient a.s.
            IČ: 87654321
            DIČ: CZ87654321

            Datum vystavení: 10.03.2026
            Datum splatnosti: 24.03.2026
            DUZP: 10.03.2026
            Variabilní symbol: 2026042

            Bankovní účet: 1265098001/5500
            IBAN: CZ5855000000001265098001

            Základ daně: 10 000,00
            DPH celkem: 2 100,00
            Celkem k úhradě: 12 100,00 Kč
            """;

        var result = _service.Extract(text);

        result.Source.ShouldBe(EExtractionSource.RegexFallback);
        result.DocumentNumber.ShouldBe("FV2026042");
        result.IssueDate.ShouldBe(new DateTime(2026, 3, 10));
        result.DueDate.ShouldBe(new DateTime(2026, 3, 24));
        result.TaxableSupplyDate.ShouldBe(new DateTime(2026, 3, 10));
        result.VariableSymbol.ShouldBe("2026042");
        result.BankAccountNumber.ShouldBe("1265098001/5500");
        result.IBAN.ShouldBe("CZ5855000000001265098001");
        result.TotalBeforeVat.ShouldBe(10000.00m);
        result.TotalVat.ShouldBe(2100.00m);
        result.TotalAmount.ShouldBe(12100.00m);
        result.Currency.ShouldBe("CZK");
        result.IssuerRegistrationNumber.ShouldBe("12345678");
        result.RecipientRegistrationNumber.ShouldBe("87654321");
        result.IssuerTaxNumber.ShouldBe("CZ12345678");
        result.RecipientTaxNumber.ShouldBe("CZ87654321");
    }

    // ─── Edge cases ──────────────────────────────────────────────────────

    [Fact]
    public void Extract_EmptyText_ReturnsEmptyData()
    {
        var result = _service.Extract("");
        result.ShouldNotBeNull();
        result.Source.ShouldBe(EExtractionSource.RegexFallback);
        result.DocumentNumber.ShouldBeNull();
        result.TotalAmount.ShouldBeNull();
    }

    [Fact]
    public void Extract_NoMatchingPatterns_AllFieldsNull()
    {
        var result = _service.Extract("Lorem ipsum dolor sit amet, consectetur adipiscing elit.");
        result.ShouldNotBeNull();
        result.DocumentNumber.ShouldBeNull();
        result.IssueDate.ShouldBeNull();
        result.TotalAmount.ShouldBeNull();
        result.VariableSymbol.ShouldBeNull();
    }

    [Fact]
    public void Extract_Items_AlwaysNull()
    {
        // Regex extractor cannot extract line items — this is AI-only
        var text = "Faktura č. FV001\nCelkem: 1000 Kč";
        var result = _service.Extract(text);
        result.Items.ShouldBeNull();
    }
}

using System.Text;
using Fakvio.Infrastructure.Import;
using Shouldly;
using Xunit;

namespace Fakvio.Tests.Unit;

/// <summary>
/// Tests for the tolerant CSV reader used by import features (N6.1).
/// Fixtures here are synthetic (no real Fakturoid/iDoklad exports were available) — see
/// DEVGUIDE.md §4.14 and the Story N6 spec for context.
/// </summary>
public class CsvTableTests
{
    static CsvTableTests()
    {
        // Needed here because the test builds a Windows-1250 fixture directly;
        // CsvTable itself registers the provider lazily only when it actually needs cp1250.
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
    }

    private static Stream ToStream(string text, Encoding? encoding = null, bool withBom = false)
    {
        encoding ??= Encoding.UTF8;
        var bytes = withBom
            ? Encoding.UTF8.GetPreamble().Concat(encoding.GetBytes(text)).ToArray()
            : encoding.GetBytes(text);
        return new MemoryStream(bytes);
    }

    [Fact]
    public void Parse_SemicolonDelimiter_ReadsHeadersAndRows()
    {
        var csv = "Jméno;IČO;Email\r\nACME s.r.o.;12345678;info@acme.cz\r\n";

        var table = CsvTable.Parse(ToStream(csv));

        table.Headers.ShouldBe(new[] { "jmeno", "ico", "email" });
        table.Rows.Count.ShouldBe(1);
        table.Rows[0]["jmeno"].ShouldBe("ACME s.r.o.");
        table.Rows[0]["ico"].ShouldBe("12345678");
        table.Rows[0]["email"].ShouldBe("info@acme.cz");
    }

    [Fact]
    public void Parse_CommaDelimiter_IsDetectedAutomatically()
    {
        var csv = "Name,RegistrationNumber\nBeta Inc,87654321\n";

        var table = CsvTable.Parse(ToStream(csv));

        table.Headers.ShouldBe(new[] { "name", "registrationnumber" });
        table.Rows[0]["name"].ShouldBe("Beta Inc");
    }

    [Fact]
    public void Parse_TabDelimiter_IsDetectedAutomatically()
    {
        var csv = "Name\tCity\nGamma\tPrague\n";

        var table = CsvTable.Parse(ToStream(csv));

        table.Headers.ShouldBe(new[] { "name", "city" });
        table.Rows[0]["city"].ShouldBe("Prague");
    }

    [Fact]
    public void Parse_QuotedFieldWithDelimiterAndNewline_IsReadAsSingleField()
    {
        var csv = "Name;Note\r\n\"Acme, s.r.o.\";\"Line one\nLine two\"\r\n";

        var table = CsvTable.Parse(ToStream(csv));

        table.Rows[0]["name"].ShouldBe("Acme, s.r.o.");
        table.Rows[0]["note"].ShouldBe("Line one\nLine two");
    }

    [Fact]
    public void Parse_EscapedDoubleQuote_IsUnescaped()
    {
        var csv = "Name;Note\r\n\"Say \"\"hello\"\"\";fine\r\n";

        var table = CsvTable.Parse(ToStream(csv));

        table.Rows[0]["name"].ShouldBe("Say \"hello\"");
    }

    [Fact]
    public void Parse_Windows1250Encoding_DecodesCzechCharacters()
    {
        var csv = "Name;City\r\nŽluťoučký;Č. Budějovice\r\n";
        var cp1250 = Encoding.GetEncoding(1250);

        var table = CsvTable.Parse(ToStream(csv, cp1250));

        table.Rows[0]["name"].ShouldBe("Žluťoučký");
        table.Rows[0]["city"].ShouldBe("Č. Budějovice");
    }

    [Fact]
    public void Parse_Utf8WithBom_StripsBomAndDecodesCorrectly()
    {
        var csv = "Name;City\r\nŽluťoučký;Praha\r\n";

        var table = CsvTable.Parse(ToStream(csv, Encoding.UTF8, withBom: true));

        table.Rows[0]["name"].ShouldBe("Žluťoučký");
    }

    [Fact]
    public void Parse_BlankLines_AreSkipped()
    {
        var csv = "Name;City\r\nAcme;Praha\r\n\r\n\r\nBeta;Brno\r\n";

        var table = CsvTable.Parse(ToStream(csv));

        table.Rows.Count.ShouldBe(2);
    }

    [Fact]
    public void Parse_UnknownHeaderNormalization_TrimsLowercasesAndStripsDiacritics()
    {
        Assert.Equal("cislo uctu", CsvTable.NormalizeHeader(" Číslo účtu "));
    }

    [Fact]
    public void Parse_EmptyFile_Throws()
    {
        Should.Throw<CsvParseException>(() => CsvTable.Parse(new MemoryStream()));
    }

    [Fact]
    public void Parse_TooManyRows_Throws()
    {
        var sb = new StringBuilder("Name\r\n");
        for (var i = 0; i < CsvTable.MaxRowCount + 1; i++)
        {
            sb.Append("Row").Append(i).Append("\r\n");
        }

        Should.Throw<CsvParseException>(() => CsvTable.Parse(ToStream(sb.ToString())));
    }

    [Fact]
    public void Parse_FileTooLarge_Throws()
    {
        // One oversized field is enough to trip the byte-size guard without needing MaxRowCount rows.
        var huge = new string('x', CsvTable.MaxFileSizeBytes + 1);
        var csv = $"Name\r\n{huge}\r\n";

        Should.Throw<CsvParseException>(() => CsvTable.Parse(ToStream(csv)));
    }
}

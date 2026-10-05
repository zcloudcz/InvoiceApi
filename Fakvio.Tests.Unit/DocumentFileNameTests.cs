using Fakvio.Contracts.Common;
using Fakvio.Domain.Entities;
using Fakvio.Domain.Enums;
using Fakvio.Infrastructure.Data;
using Fakvio.Infrastructure.Service;
using Microsoft.EntityFrameworkCore;
using Shouldly;

namespace Fakvio.Tests.Unit;

/// <summary>
/// The pure builder: placeholders, localized defaults, sanitization, fallbacks and validation.
/// No database, no clock — every input is passed in.
/// </summary>
public class DocumentFileNameBuilderTests
{
    private static DocumentFileNameValues Values(
        EDocumentType type = EDocumentType.Invoice,
        string? language = "cs",
        string? number = "2026001") =>
        new(type, language, InvoiceId: 42, number, VariableSymbol: "VS123",
            IssueDate: new DateTime(2026, 3, 5), ClientName: "Acme s.r.o.", IssuerName: "My Firm",
            TaxDate: new DateTime(2026, 3, 1), DueDate: new DateTime(2026, 3, 19));

    [Theory]
    [InlineData("{{DocumentType}}", "Faktura.pdf")]
    [InlineData("{{DocumentNumber}}", "2026001.pdf")]
    [InlineData("{{VariableSymbol}}", "VS123.pdf")]
    [InlineData("{{IssueDate}}", "2026-03-05.pdf")]
    [InlineData("{{TaxDate}}", "2026-03-01.pdf")]
    [InlineData("{{DueDate}}", "2026-03-19.pdf")]
    [InlineData("{{Year}}", "2026.pdf")]
    [InlineData("{{Month}}", "03.pdf")] // two digits
    [InlineData("{{ClientName}}", "Acme s.r.o.pdf")] // the name's trailing dot is trimmed, then ".pdf" is appended
    [InlineData("{{IssuerName}}", "My Firm.pdf")]
    public void Build_EachPlaceholder_IsReplaced(string format, string expected)
    {
        DocumentFileName.Build(format, Values(), DocumentFileName.PdfExtension).ShouldBe(expected);
    }

    [Fact]
    public void Build_PlaceholdersAreCaseInsensitive_AndTolerateInnerWhitespace()
    {
        DocumentFileName.Build("{{ documentnumber }}-{{YEAR}}", Values(), ".pdf").ShouldBe("2026001-2026.pdf");
    }

    [Theory]
    [InlineData(EDocumentType.Invoice, "cs", "Faktura_2026001.pdf")]
    [InlineData(EDocumentType.CreditNote, "cs", "Dobropis_2026001.pdf")]
    [InlineData(EDocumentType.Proforma, "cs", "Proforma_2026001.pdf")]
    [InlineData(EDocumentType.TaxReceiptForAdvance, "cs", "Danovy_doklad_2026001.pdf")]
    [InlineData(EDocumentType.Invoice, "en", "Invoice_2026001.pdf")]
    [InlineData(EDocumentType.CreditNote, "en", "CreditNote_2026001.pdf")]
    [InlineData(EDocumentType.Proforma, "en", "Proforma_2026001.pdf")]
    [InlineData(EDocumentType.TaxReceiptForAdvance, "en", "TaxReceipt_2026001.pdf")]
    [InlineData(EDocumentType.Invoice, "EN", "Invoice_2026001.pdf")] // language is case-insensitive
    [InlineData(EDocumentType.Invoice, "de", "Faktura_2026001.pdf")] // unknown language -> app default (cs)
    [InlineData(EDocumentType.Invoice, null, "Faktura_2026001.pdf")]
    public void Build_NoFormat_UsesLocalizedSystemDefault(EDocumentType type, string? language, string expected)
    {
        DocumentFileName.Build(null, Values(type, language), DocumentFileName.PdfExtension).ShouldBe(expected);
    }

    [Theory]
    [InlineData(".pdf")]
    [InlineData(".isdoc")]
    [InlineData(".xml")]
    public void Build_AppendsTheGivenExtension(string extension)
    {
        DocumentFileName.Build("{{DocumentNumber}}", Values(), extension).ShouldBe("2026001" + extension);
    }

    [Fact]
    public void Build_DraftWithoutNumber_UsesInvoiceId()
    {
        DocumentFileName.Build("{{DocumentType}}_{{DocumentNumber}}", Values(number: null), ".pdf")
            .ShouldBe("Faktura_42.pdf");
    }

    [Fact]
    public void Build_InvalidWindowsCharacters_AreReplaced()
    {
        // The client name comes from user data and may contain anything.
        var values = Values() with { ClientName = "a/b\\c:d*e?f\"g<h>i|j" };

        var name = DocumentFileName.Build("{{ClientName}}", values, ".pdf");

        name.ShouldBe("a_b_c_d_e_f_g_h_i_j.pdf");
        name.IndexOfAny(['/', '\\', ':', '*', '?', '"', '<', '>', '|']).ShouldBe(-1);
    }

    [Fact]
    public void Build_ControlCharacters_AreReplaced()
    {
        var values = Values() with { ClientName = "line1\r\nline2\t" };

        DocumentFileName.Build("{{ClientName}}", values, ".pdf").ShouldBe("line1__line2_.pdf"); // CR, LF and the tab each become one underscore
    }

    [Fact]
    public void Build_TrimsSpacesAndDotsAtTheEnds_ButKeepsInnerSpaces()
    {
        var values = Values() with { ClientName = "  . My  Client .. " };

        DocumentFileName.Build("{{ClientName}}", values, ".pdf").ShouldBe("My  Client.pdf");
    }

    [Fact]
    public void Build_LongResult_IsCappedBeforeTheExtension()
    {
        var values = Values() with { ClientName = new string('x', 400) };

        var name = DocumentFileName.Build("{{ClientName}}", values, ".isdoc");

        name.ShouldBe(new string('x', DocumentFileName.MaxLength) + ".isdoc");
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("{{Unknown}}")] // renders to nothing
    [InlineData(" . ")] // only spaces and dots, trimmed away
    public void Build_FormatThatRendersEmpty_FallsBackToSystemDefault(string format)
    {
        DocumentFileName.Build(format, Values(), ".pdf").ShouldBe("Faktura_2026001.pdf");
    }

    [Fact]
    public void Build_EmptyPlaceholderValues_FallBackToSystemDefault()
    {
        // Only {{VariableSymbol}} and it is empty -> nothing left -> default name.
        var values = Values() with { VariableSymbol = null };

        DocumentFileName.Build("{{VariableSymbol}}", values, ".pdf").ShouldBe("Faktura_2026001.pdf");
    }

    [Fact]
    public void Build_UnknownPlaceholder_IsDropped_NotKeptLiterally()
    {
        DocumentFileName.Build("A_{{Nope}}_{{DocumentNumber}}", Values(), ".pdf").ShouldBe("A__2026001.pdf");
    }

    [Theory]
    [InlineData("{{IssueDate:yyyyMMdd}}", "20260305.pdf")]
    [InlineData("{{IssueDate:dd.MM.yyyy}}", "05.03.2026.pdf")]
    [InlineData("{{IssueDate:yyMM}}", "2603.pdf")]
    [InlineData("{{TaxDate:yyyyMMdd}}", "20260301.pdf")]
    [InlineData("{{TaxDate:dd.MM.yyyy}}", "01.03.2026.pdf")]
    [InlineData("{{DueDate:yyyyMMdd}}", "20260319.pdf")]
    [InlineData("{{DueDate:yyMM}}", "2603.pdf")]
    [InlineData("{{issuedate:yyyy_MM_dd}}", "2026_03_05.pdf")] // key is case-insensitive
    [InlineData("{{ IssueDate:yyyyMMdd }}", "20260305.pdf")] // whitespace around the whole token is tolerated
    [InlineData("{{IssueDate:d}}", "5.pdf")] // single letter must be a custom format, not the short-date specifier
    [InlineData("{{IssueDate:M}}", "3.pdf")]
    public void Build_DateFormat_IsApplied(string format, string expected)
    {
        DocumentFileName.Build(format, Values(), DocumentFileName.PdfExtension).ShouldBe(expected);
    }

    [Theory]
    [InlineData("{{IssueDate:HH}}")]
    [InlineData("{{IssueDate:}}")]
    [InlineData("{{IssueDate:mm}}")] // case matters: only M (month) is allowed
    [InlineData("{{IssueDate:yyyy/MM}}")]
    [InlineData("{{IssueDate:'x'}}")]
    [InlineData("{{IssueDate:\\d}}")]
    [InlineData("{{IssueDate:yyyy:MM}}")]
    public void Build_InvalidDateFormat_DoesNotThrow_AndUsesDefaultFormat(string format)
    {
        DocumentFileName.Build(format, Values(), ".pdf").ShouldBe("2026-03-05.pdf");
    }

    [Fact]
    public void Build_NullDates_RenderEmpty_AlsoWithAFormat()
    {
        var values = Values() with { IssueDate = null, TaxDate = null, DueDate = null };

        DocumentFileName.Build("A{{IssueDate:yyyyMMdd}}_{{TaxDate}}_{{DueDate:yyMM}}B", values, ".pdf")
            .ShouldBe("A__B.pdf");
    }

    [Theory]
    [InlineData("{{IssueDate:HH}}", "IssueDate:HH")]
    [InlineData("{{TaxDate:}}", "TaxDate:")]
    [InlineData("{{DueDate:dd/MM}}", "DueDate:dd/MM")]
    [InlineData("{{IssueDate:mm}}", "IssueDate:mm")]
    [InlineData("{{IssueDate:abc}}", "IssueDate:abc")]
    [InlineData("{{ClientName:xyz}}", "ClientName:xyz")] // formats exist only on date placeholders
    [InlineData("{{Year:yy}}", "Year:yy")]
    [InlineData("{{Nope:yyyy}}", "Nope:yyyy")]
    public void Validate_InvalidFormatSuffix_IsReported(string format, string expected)
    {
        DocumentFileName.Validate(format).ShouldBe([expected]);
    }

    [Theory]
    [InlineData("{{IssueDate:yyyyMMdd}}")]
    [InlineData("{{DueDate:dd.MM.yyyy}}_{{TaxDate:yy-MM-dd}}")]
    [InlineData("{{IssueDate:yyyy_MM}}")]
    public void Validate_ValidDateFormat_ReturnsNothing(string format)
    {
        DocumentFileName.Validate(format).ShouldBeEmpty();
    }

    [Fact]
    public void ValidateFormats_InvalidDateFormat_ExplainsAllowedCharacters()
    {
        var message = DocumentFileName.ValidateFormats("{{DocumentNumber}}", "{{IssueDate:HH}}");

        message.ShouldNotBeNull();
        message.ShouldContain("Invalid date format 'HH' in {{IssueDate:HH}}");
        message.ShouldContain("Allowed characters: y M d - _ .");
    }

    [Fact]
    public void ValidateFormats_FormatOnNonDatePlaceholder_IsRejected()
    {
        var message = DocumentFileName.ValidateFormats("{{ClientName:xyz}}");

        message.ShouldNotBeNull();
        message.ShouldContain("ClientName:xyz");
    }

    [Fact]
    public void PlaceholderKeys_IncludeTaxAndDueDate()
    {
        DocumentFileName.PlaceholderKeys.ShouldContain("TaxDate");
        DocumentFileName.PlaceholderKeys.ShouldContain("DueDate");
    }

    [Fact]
    public void Validate_ReportsUnknownKeys_EachOnce()
    {
        var unknown = DocumentFileName.Validate("{{DocumentNumber}}_{{Foo}}_{{foo}}_{{Bar}}");

        unknown.ShouldBe(["Foo", "Bar"]);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("{{DocumentType}}_{{DocumentNumber}}")]
    [InlineData("{{Year}}-{{Month}} {{ClientName}}")]
    public void Validate_ValidOrEmptyFormat_ReturnsNoKeys(string? format)
    {
        DocumentFileName.Validate(format).ShouldBeEmpty();
    }

    [Fact]
    public void ValidateFormats_UnknownKeyOrTooLong_ReturnsMessage()
    {
        DocumentFileName.ValidateFormats("{{DocumentNumber}}", null).ShouldBeNull();

        var unknown = DocumentFileName.ValidateFormats("{{DocumentNumber}}", "{{Typo}}");
        unknown.ShouldNotBeNull();
        unknown.ShouldContain("Typo");

        DocumentFileName.ValidateFormats(new string('a', DocumentFileName.MaxFormatLength + 1))
            .ShouldNotBeNull();
    }

    [Fact]
    public void ResolveFormat_ClientWinsOverIssuer_BlankFallsThrough()
    {
        DocumentFileName.ResolveFormat("client", "issuer").ShouldBe("client");
        DocumentFileName.ResolveFormat(null, "issuer").ShouldBe("issuer");
        DocumentFileName.ResolveFormat("  ", "issuer").ShouldBe("issuer");
        DocumentFileName.ResolveFormat("", "").ShouldBeNull();
    }
}

/// <summary>
/// The resolution chain (client -> issuer -> system default) against a real (in-memory) database.
/// Data is seeded through a SEPARATE context from the one the service reads with, so a missing
/// Include/join cannot be hidden by EF's change-tracker fix-up.
/// </summary>
public class DocumentFileNameServiceTests : IDisposable
{
    private const long IssuerId = 1;
    private readonly string _dbName = Guid.NewGuid().ToString();

    private TenantDbContext NewContext() =>
        new(new DbContextOptionsBuilder<TenantDbContext>().UseInMemoryDatabase(_dbName).Options);

    public void Dispose()
    {
        using var db = NewContext();
        db.Database.EnsureDeleted();
    }

    /// <summary>Seeds issuer (id 1) + customer + one invoice of the given type; returns the invoice id.</summary>
    private long Seed(
        EDocumentType type,
        string? clientFormat = null,
        string? issuerFormat = null,
        string language = "cs",
        long invoiceId = 10,
        long customerId = 2)
    {
        using var db = NewContext();

        if (!db.Client.Any(c => c.Id == IssuerId))
        {
            db.Currency.Add(new Currency { Id = 1, Code = "CZK", Symbol = "Kc", Name = "Czech Koruna", IsActive = true });
            db.Client.Add(new Client { Id = IssuerId, CompanyName = "Issuer Ltd", RegistrationNumber = "111", IsIssuer = true, IsActive = true });
        }

        // Format of the chosen document type goes into the right BillingSettings column.
        db.Client.Add(new Client
        {
            Id = customerId, CompanyName = "Customer Ltd", RegistrationNumber = $"22{customerId}",
            IsActive = true, Language = language
        });
        db.SaveChanges();

        if (clientFormat != null)
            db.BillingSettings.Add(WithFormat(new BillingSettings { ClientId = customerId }, type, clientFormat));
        if (issuerFormat != null && !db.BillingSettings.Any(b => b.ClientId == IssuerId))
            db.BillingSettings.Add(WithFormat(new BillingSettings { ClientId = IssuerId }, type, issuerFormat));

        db.Invoice.Add(new Invoice
        {
            Id = invoiceId, DocumentType = type, Status = EInvoiceStatus.Completed,
            DocumentNumber = "2026001", VariableSymbol = "9876", IssueDate = new DateTime(2026, 3, 5),
            IssuerId = IssuerId, ClientId = customerId, CurrencyId = 1
        });
        db.SaveChanges();
        return invoiceId;
    }

    private static BillingSettings WithFormat(BillingSettings bs, EDocumentType type, string format)
    {
        switch (type)
        {
            case EDocumentType.CreditNote: bs.CreditNoteFileNameFormat = format; break;
            case EDocumentType.Proforma: bs.ProformaFileNameFormat = format; break;
            case EDocumentType.TaxReceiptForAdvance: bs.TaxReceiptFileNameFormat = format; break;
            default: bs.InvoiceFileNameFormat = format; break;
        }

        return bs;
    }

    [Fact]
    public async Task NoFormatAnywhere_UsesLocalizedSystemDefault()
    {
        var id = Seed(EDocumentType.Invoice);

        using var db = NewContext();
        var name = await new DocumentFileNameService(db).GetFileNameAsync(id, ".pdf");

        name.ShouldBe("Faktura_2026001.pdf");
    }

    [Fact]
    public async Task ClientFormat_Wins_OverIssuerFormat()
    {
        var id = Seed(EDocumentType.Invoice, clientFormat: "C_{{DocumentNumber}}", issuerFormat: "I_{{DocumentNumber}}");

        using var db = NewContext();
        var name = await new DocumentFileNameService(db).GetFileNameAsync(id, ".pdf");

        name.ShouldBe("C_2026001.pdf");
    }

    [Fact]
    public async Task NoClientFormat_FallsBackToIssuerFormat()
    {
        var id = Seed(EDocumentType.Invoice, issuerFormat: "{{IssuerName}}_{{Year}}-{{DocumentNumber}}");

        using var db = NewContext();
        var name = await new DocumentFileNameService(db).GetFileNameAsync(id, ".isdoc");

        name.ShouldBe("Issuer Ltd_2026-2026001.isdoc");
    }

    [Fact]
    public async Task ClientHasSettingsButNoFormat_StillFallsBackToIssuer()
    {
        // A BillingSettings row with only another document type's format must not block inheritance.
        var id = Seed(EDocumentType.Invoice, issuerFormat: "I_{{DocumentNumber}}");
        using (var db = NewContext())
        {
            db.BillingSettings.Add(new BillingSettings { ClientId = 2, CreditNoteFileNameFormat = "CN_{{DocumentNumber}}" });
            db.SaveChanges();
        }

        using var read = NewContext();
        (await new DocumentFileNameService(read).GetFileNameAsync(id, ".pdf")).ShouldBe("I_2026001.pdf");
    }

    [Theory]
    [InlineData(EDocumentType.Invoice)]
    [InlineData(EDocumentType.CreditNote)]
    [InlineData(EDocumentType.Proforma)]
    [InlineData(EDocumentType.TaxReceiptForAdvance)]
    public async Task EachDocumentType_ReadsItsOwnFormat(EDocumentType type)
    {
        var id = Seed(type, clientFormat: "T_{{VariableSymbol}}");

        using var db = NewContext();
        (await new DocumentFileNameService(db).GetFileNameAsync(id, ".pdf")).ShouldBe("T_9876.pdf");
    }

    [Fact]
    public async Task OtherDocumentTypesFormat_IsNotUsed()
    {
        // Only the credit note format is configured; an invoice must still get the default name.
        var id = Seed(EDocumentType.Invoice);
        using (var db = NewContext())
        {
            db.BillingSettings.Add(new BillingSettings { ClientId = 2, CreditNoteFileNameFormat = "CN_{{DocumentNumber}}" });
            db.SaveChanges();
        }

        using var read = NewContext();
        (await new DocumentFileNameService(read).GetFileNameAsync(id, ".pdf")).ShouldBe("Faktura_2026001.pdf");
    }

    [Fact]
    public async Task EnglishClient_GetsEnglishDocumentTypeWord()
    {
        var id = Seed(EDocumentType.CreditNote, language: "en");

        using var db = NewContext();
        (await new DocumentFileNameService(db).GetFileNameAsync(id, ".pdf")).ShouldBe("CreditNote_2026001.pdf");
    }

    [Fact]
    public async Task UnknownInvoice_ThrowsKeyNotFound()
    {
        using var db = NewContext();

        await Should.ThrowAsync<KeyNotFoundException>(
            () => new DocumentFileNameService(db).GetFileNameAsync(999, ".pdf"));
    }

    [Fact]
    public async Task Bulk_ResolvesEveryInvoiceWithItsOwnClientFormat_InOneCall()
    {
        Seed(EDocumentType.Invoice, clientFormat: "A_{{DocumentNumber}}", invoiceId: 10, customerId: 2);
        Seed(EDocumentType.Invoice, issuerFormat: "ISS_{{DocumentNumber}}", invoiceId: 11, customerId: 3);

        using var db = NewContext();
        var names = await new DocumentFileNameService(db).GetFileNamesAsync([10, 11, 404], ".pdf");

        names.Count.ShouldBe(2); // unknown id 404 is simply absent
        names[10].ShouldBe("A_2026001.pdf");
        names[11].ShouldBe("ISS_2026001.pdf");
    }
}

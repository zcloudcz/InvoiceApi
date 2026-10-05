using Fakvio.Domain.Enums;
using Fakvio.Infrastructure.Service;
using Shouldly;

namespace Fakvio.Tests.Unit;

/// <summary>
/// EmailService.BuildFallbackEmail is used when no content template exists at all. It must follow the client's
/// language like the seeded templates do: Czech by default, English only for "en".
/// </summary>
public class EmailFallbackLanguageTests
{
    private static readonly Dictionary<string, string> Placeholders = new()
    {
        ["InvoiceNumber"] = "2026-001",
        ["CompanyName"] = "Test s.r.o.",
        ["TotalWithVat"] = "12 100,00",
        ["CurrencyCode"] = "CZK",
        ["DueDate"] = "15.06.2026",
        ["AppName"] = "Fakvio",
        ["FullName"] = "Jan Novák",
        ["InvitationLink"] = "https://example.test/set-password?t=abc",
    };

    // (type, a word that only the Czech text contains, a word that only the English text contains)
    public static readonly TheoryData<EContentTemplateType, string, string> Types = new()
    {
        { EContentTemplateType.InvoiceEmail, "Faktura", "Invoice" },
        { EContentTemplateType.CreditNoteEmail, "Dobropis", "Credit" },
        { EContentTemplateType.AdvanceInvoiceEmail, "Zálohová", "Advance" },
        { EContentTemplateType.TaxReceiptForAdvanceEmail, "Daňový doklad", "Tax receipt" },
        { EContentTemplateType.InvitationEmail, "Pozvánka", "Invitation" },
        { EContentTemplateType.ReminderEmail, "Upomínka", "Payment reminder" },
    };

    [Theory]
    [MemberData(nameof(Types))]
    public void Czech_IsTheDefault_AndEnglishOnlyForEn(EContentTemplateType type, string czechWord, string englishWord)
    {
        foreach (var language in new[] { "cs", "de", "" })
        {
            var (subject, body) = EmailService.BuildFallbackEmail(type, Placeholders, language);
            subject.ShouldContain(czechWord, Case.Insensitive, $"{type}/{language}");
            subject.ShouldNotContain(englishWord, Case.Insensitive, $"{type}/{language}");
        }

        foreach (var language in new[] { "en", "EN" })
        {
            var (subject, body) = EmailService.BuildFallbackEmail(type, Placeholders, language);
            subject.ShouldContain(englishWord, Case.Insensitive, $"{type}/{language}");
            subject.ShouldNotContain(czechWord, Case.Insensitive, $"{type}/{language}");
        }
    }

    [Theory]
    [MemberData(nameof(Types))]
    public void BothLanguages_SubstitutePlaceholders(EContentTemplateType type, string czechWord, string englishWord)
    {
        foreach (var language in new[] { "cs", "en" })
        {
            var (subject, body) = EmailService.BuildFallbackEmail(type, Placeholders, language);
            (subject + body).ShouldNotContain("{{");

            // Invitation carries the link and name, the others the document number.
            var expected = type == EContentTemplateType.InvitationEmail ? "https://example.test/set-password?t=abc" : "2026-001";
            body.ShouldContain(expected);
        }
    }

    [Fact]
    public void CreditNote_HasItsOwnText_NotTheInvoiceOne()
    {
        // Used to fall through to the invoice text ("Invoice ... Please find the attached invoice").
        var (subject, body) = EmailService.BuildFallbackEmail(EContentTemplateType.CreditNoteEmail, Placeholders, "en");

        subject.ShouldStartWith("Credit note");
        body.ShouldContain("credit note");
    }
}

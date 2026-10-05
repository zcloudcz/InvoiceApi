using Fakvio.Domain.Entities;
using Fakvio.Domain.Enums;

namespace Fakvio.Infrastructure.Data;

/// <summary>
/// Default e-mail texts (subject + HTML body) shared by MasterDbContext and TenantDbContext seeds,
/// the data migration <c>LocalizeDefaultEmailTemplates</c> and the unit tests, so the text lives in one place.
///
/// Every e-mail type has a Czech ("cs") and an English ("en") default. Which one is sent is decided by
/// <c>ContentTemplateService.GetDefaultByTypeAsync(type, language)</c> from the client's language.
///
/// Placeholders (<c>{{InvoiceNumber}}</c> etc.) are replaced at render time — keep them identical in both languages.
/// </summary>
public static class DefaultEmailTemplates
{
    // ── Czech (replaces the English text that was wrongly seeded under Language = "cs") ──

    public const string InvoiceCsSubject = "Faktura {{InvoiceNumber}} od {{CompanyName}}";
    public const string InvoiceCsBody = """<div style="font-family: Arial, sans-serif; max-width: 600px; margin: 0 auto;"><h2 style="color: #1976D2;">Faktura {{InvoiceNumber}}</h2><p>Vážený zákazníku,</p><p>v příloze naleznete fakturu <strong>{{InvoiceNumber}}</strong>.</p><p><strong>Celkem k úhradě:</strong> {{TotalWithVat}} {{CurrencyCode}}</p><p><strong>Datum splatnosti:</strong> {{DueDate}}</p><br/><p>Děkujeme za spolupráci.</p><hr style="border: none; border-top: 1px solid #eee; margin: 20px 0;" /><p style="color: #999; font-size: 12px;">{{CompanyName}}</p></div>""";

    public const string CreditNoteCsSubject = "Dobropis {{InvoiceNumber}} od {{CompanyName}}";
    public const string CreditNoteCsBody = """<div style="font-family: Arial, sans-serif; max-width: 600px; margin: 0 auto;"><h2 style="color: #1976D2;">Dobropis {{InvoiceNumber}}</h2><p>Vážený zákazníku,</p><p>v příloze naleznete dobropis <strong>{{InvoiceNumber}}</strong>.</p><p><strong>Celkem:</strong> {{TotalWithVat}} {{CurrencyCode}}</p><br/><p>Děkujeme za spolupráci.</p><hr style="border: none; border-top: 1px solid #eee; margin: 20px 0;" /><p style="color: #999; font-size: 12px;">{{CompanyName}}</p></div>""";

    public const string InvitationCsSubject = "Pozvánka do {{AppName}} – nastavte si heslo";
    public const string InvitationCsBody = """<div style="font-family: Arial, sans-serif; max-width: 600px; margin: 0 auto;"><h2 style="color: #1976D2;">Vítejte v {{AppName}}</h2><p>Dobrý den, <strong>{{FullName}}</strong>,</p><p>byli jste pozváni do aplikace {{AppName}}. Heslo si nastavíte kliknutím na tlačítko níže:</p><div style="text-align: center; margin: 30px 0;"><a href="{{InvitationLink}}" style="background-color: #1976D2; color: white; padding: 14px 28px; text-decoration: none; border-radius: 4px; font-size: 16px;">Nastavit heslo</a></div><p style="color: #666; font-size: 14px;">Odkaz je platný 48 hodin.</p><hr style="border: none; border-top: 1px solid #eee; margin: 20px 0;" /><p style="color: #999; font-size: 12px;">{{AppName}}</p></div>""";

    public const string ReminderCsSubject = "Upomínka – faktura {{InvoiceNumber}} po splatnosti";
    public const string ReminderCsBody = """<div style="font-family: Arial, sans-serif; max-width: 600px; margin: 0 auto;"><h2 style="color: #E65100;">Upomínka k úhradě</h2><p>Vážený zákazníku,</p><p>dovolujeme si Vás upozornit, že faktura <strong>{{InvoiceNumber}}</strong> je po splatnosti.</p><p><strong>Celkem k úhradě:</strong> {{TotalWithVat}} {{CurrencyCode}}</p><p><strong>Datum splatnosti:</strong> {{DueDate}}</p><p>Prosíme o úhradu v nejbližším možném termínu. Pokud jste již platbu provedli, považujte tuto upomínku za bezpředmětnou.</p><br/><p>Děkujeme.</p><hr style="border: none; border-top: 1px solid #eee; margin: 20px 0;" /><p style="color: #999; font-size: 12px;">{{CompanyName}}</p></div>""";

    // ── English ("en"): the first four are the text that used to be seeded as "cs"; the last two translate the Czech seeds ──

    public const string InvoiceEnSubject = "Invoice {{InvoiceNumber}} from {{CompanyName}}";
    public const string InvoiceEnBody = """<div style="font-family: Arial, sans-serif; max-width: 600px; margin: 0 auto;"><h2 style="color: #1976D2;">Invoice {{InvoiceNumber}}</h2><p>Dear customer,</p><p>Please find the attached invoice <strong>{{InvoiceNumber}}</strong>.</p><p><strong>Total:</strong> {{TotalWithVat}} {{CurrencyCode}}</p><p><strong>Due date:</strong> {{DueDate}}</p><br/><p>Thank you for your business.</p><hr style="border: none; border-top: 1px solid #eee; margin: 20px 0;" /><p style="color: #999; font-size: 12px;">{{CompanyName}}</p></div>""";

    public const string CreditNoteEnSubject = "Credit Note {{InvoiceNumber}} from {{CompanyName}}";
    public const string CreditNoteEnBody = """<div style="font-family: Arial, sans-serif; max-width: 600px; margin: 0 auto;"><h2 style="color: #1976D2;">Credit Note {{InvoiceNumber}}</h2><p>Dear customer,</p><p>Please find the attached credit note <strong>{{InvoiceNumber}}</strong>.</p><p><strong>Total:</strong> {{TotalWithVat}} {{CurrencyCode}}</p><br/><p>Thank you for your business.</p><hr style="border: none; border-top: 1px solid #eee; margin: 20px 0;" /><p style="color: #999; font-size: 12px;">{{CompanyName}}</p></div>""";

    public const string InvitationEnSubject = "Invitation to {{AppName}} — Set your password";
    public const string InvitationEnBody = """<div style="font-family: Arial, sans-serif; max-width: 600px; margin: 0 auto;"><h2 style="color: #1976D2;">Welcome to {{AppName}}</h2><p>Hello <strong>{{FullName}}</strong>,</p><p>You have been invited to {{AppName}}. Please set your password by clicking the button below:</p><div style="text-align: center; margin: 30px 0;"><a href="{{InvitationLink}}" style="background-color: #1976D2; color: white; padding: 14px 28px; text-decoration: none; border-radius: 4px; font-size: 16px;">Set Password</a></div><p style="color: #666; font-size: 14px;">This link is valid for 48 hours.</p><hr style="border: none; border-top: 1px solid #eee; margin: 20px 0;" /><p style="color: #999; font-size: 12px;">{{AppName}}</p></div>""";

    public const string ReminderEnSubject = "Payment reminder — Invoice {{InvoiceNumber}}";
    public const string ReminderEnBody = """<div style="font-family: Arial, sans-serif; max-width: 600px; margin: 0 auto;"><h2 style="color: #E65100;">Payment Reminder</h2><p>Dear customer,</p><p>This is a friendly reminder that invoice <strong>{{InvoiceNumber}}</strong> is overdue.</p><p><strong>Total:</strong> {{TotalWithVat}} {{CurrencyCode}}</p><p><strong>Due date:</strong> {{DueDate}}</p><p>Please arrange payment at your earliest convenience.</p><br/><p>Thank you.</p><hr style="border: none; border-top: 1px solid #eee; margin: 20px 0;" /><p style="color: #999; font-size: 12px;">{{CompanyName}}</p></div>""";

    public const string AdvanceInvoiceEnSubject = "Advance invoice {{InvoiceNumber}} from {{CompanyName}}";
    public const string AdvanceInvoiceEnBody = """<div style="font-family: Arial, sans-serif; max-width: 600px; margin: 0 auto;"><h2 style="color: #3D7A4A;">Advance Invoice {{InvoiceNumber}}</h2><p>Dear customer,</p><p>Please find the attached advance invoice <strong>{{InvoiceNumber}}</strong>.</p><p><strong>Amount due:</strong> {{TotalWithVat}} {{CurrencyCode}}</p><p><strong>Due date:</strong> {{DueDate}}</p><br/><p>Please note that an advance invoice is not a tax document. A tax document will be issued to you once the payment is received.</p><p>Thank you.</p><hr style="border: none; border-top: 1px solid #eee; margin: 20px 0;" /><p style="color: #999; font-size: 12px;">{{CompanyName}}</p></div>""";

    public const string TaxReceiptEnSubject = "Tax receipt for advance payment {{InvoiceNumber}} from {{CompanyName}}";
    public const string TaxReceiptEnBody = """<div style="font-family: Arial, sans-serif; max-width: 600px; margin: 0 auto;"><h2 style="color: #6A3D9A;">Tax Receipt for Advance Payment {{InvoiceNumber}}</h2><p>Dear customer,</p><p>Please find the attached tax receipt for the advance payment <strong>{{InvoiceNumber}}</strong>.</p><p><strong>Amount paid:</strong> {{TotalWithVat}} {{CurrencyCode}}</p><br/><p>Thank you for your payment.</p><hr style="border: none; border-top: 1px solid #eee; margin: 20px 0;" /><p style="color: #999; font-size: 12px;">{{CompanyName}}</p></div>""";

    /// <summary>
    /// The English default for each e-mail type that has a Czech default, with fixed seed Ids 12–17
    /// (Ids 1–11 are taken by the original seed). Used by BOTH contexts' HasData so the model snapshot stays identical.
    /// Existing databases get these rows from the migration (without fixed Ids), not from HasData.
    /// </summary>
    public static ContentTemplate[] EnglishVariants(DateTime seedDate) =>
    [
        English(12, "Default Invoice Email (EN)", EContentTemplateType.InvoiceEmail, InvoiceEnSubject, InvoiceEnBody, "English email body when sending an invoice to a client whose language is English.", seedDate),
        English(13, "Default Credit Note Email (EN)", EContentTemplateType.CreditNoteEmail, CreditNoteEnSubject, CreditNoteEnBody, "English email body when sending a credit note to a client whose language is English.", seedDate),
        English(14, "Default Invitation Email (EN)", EContentTemplateType.InvitationEmail, InvitationEnSubject, InvitationEnBody, "English email sent to new users.", seedDate),
        English(15, "Default Payment Reminder (EN)", EContentTemplateType.ReminderEmail, ReminderEnSubject, ReminderEnBody, "English payment reminder for overdue invoices.", seedDate),
        English(16, "Default Advance Invoice Email (EN)", EContentTemplateType.AdvanceInvoiceEmail, AdvanceInvoiceEnSubject, AdvanceInvoiceEnBody, "English email sent with an advance invoice. Tells the recipient that it is not a tax document.", seedDate),
        English(17, "Default Tax Receipt for Advance Email (EN)", EContentTemplateType.TaxReceiptForAdvanceEmail, TaxReceiptEnSubject, TaxReceiptEnBody, "English email sent with a tax receipt for a received advance payment.", seedDate),
    ];

    private static ContentTemplate English(
        long id, string name, EContentTemplateType type, string subject, string body, string description, DateTime seedDate) =>
        new()
        {
            Id = id, Name = name, Subject = subject, HtmlBody = body, TemplateType = type,
            IsDefault = true, IsActive = true, Language = "en", Description = description, CreatedAt = seedDate
        };
}

using Fakvio.Infrastructure.Data;

namespace Fakvio.Infrastructure.Migrations;

/// <summary>
/// Raw SQL shared by the Master and Tenant <c>LocalizeDefaultEmailTemplates</c> migrations.
///
/// WHY raw SQL instead of EF's generated UpdateData/InsertData:
///  * UpdateData is keyed by Id. Tenant ContentTemplate Ids come from <c>TenantProvisioningService</c>
///    (rows are re-inserted from master with a restarted sequence), so a seed Id does not identify the same
///    row everywhere, and an Id-keyed UPDATE would also overwrite templates a user has edited.
///  * InsertData with fixed Ids (12–17) can collide with tenant rows that already own those Ids.
///
/// So the UPDATEs only touch rows that are still byte-for-byte the ORIGINAL (wrongly English) seed, and the
/// INSERTs let the identity column assign the Id and skip a type that already has an English template.
///
/// The "original" texts below are frozen on purpose: they must keep matching what old databases contain even if
/// <see cref="DefaultEmailTemplates"/> is edited later. Table names are unqualified because tenant migrations
/// run with <c>search_path</c> pointing at the tenant schema (same style as the other raw-SQL migrations).
/// </summary>
internal static class LocalizeDefaultEmailTemplatesSql
{
    // ── Frozen original seed text (English, but stored with Language = 'cs') ──

    private const string OriginalInvoiceSubject = "Invoice {{InvoiceNumber}} from {{CompanyName}}";
    private const string OriginalInvoiceBody = """<div style="font-family: Arial, sans-serif; max-width: 600px; margin: 0 auto;"><h2 style="color: #1976D2;">Invoice {{InvoiceNumber}}</h2><p>Dear customer,</p><p>Please find the attached invoice <strong>{{InvoiceNumber}}</strong>.</p><p><strong>Total:</strong> {{TotalWithVat}} {{CurrencyCode}}</p><p><strong>Due date:</strong> {{DueDate}}</p><br/><p>Thank you for your business.</p><hr style="border: none; border-top: 1px solid #eee; margin: 20px 0;" /><p style="color: #999; font-size: 12px;">{{CompanyName}}</p></div>""";

    private const string OriginalCreditNoteSubject = "Credit Note {{InvoiceNumber}} from {{CompanyName}}";
    private const string OriginalCreditNoteBody = """<div style="font-family: Arial, sans-serif; max-width: 600px; margin: 0 auto;"><h2 style="color: #1976D2;">Credit Note {{InvoiceNumber}}</h2><p>Dear customer,</p><p>Please find the attached credit note <strong>{{InvoiceNumber}}</strong>.</p><p><strong>Total:</strong> {{TotalWithVat}} {{CurrencyCode}}</p><br/><p>Thank you for your business.</p><hr style="border: none; border-top: 1px solid #eee; margin: 20px 0;" /><p style="color: #999; font-size: 12px;">{{CompanyName}}</p></div>""";

    private const string OriginalInvitationSubject = "Invitation to {{AppName}} — Set your password";
    private const string OriginalInvitationBody = """<div style="font-family: Arial, sans-serif; max-width: 600px; margin: 0 auto;"><h2 style="color: #1976D2;">Welcome to {{AppName}}</h2><p>Hello <strong>{{FullName}}</strong>,</p><p>You have been invited to {{AppName}}. Please set your password by clicking the button below:</p><div style="text-align: center; margin: 30px 0;"><a href="{{InvitationLink}}" style="background-color: #1976D2; color: white; padding: 14px 28px; text-decoration: none; border-radius: 4px; font-size: 16px;">Set Password</a></div><p style="color: #666; font-size: 14px;">This link is valid for 48 hours.</p><hr style="border: none; border-top: 1px solid #eee; margin: 20px 0;" /><p style="color: #999; font-size: 12px;">{{AppName}}</p></div>""";

    private const string OriginalReminderSubject = "Payment reminder — Invoice {{InvoiceNumber}}";
    private const string OriginalReminderBody = """<div style="font-family: Arial, sans-serif; max-width: 600px; margin: 0 auto;"><h2 style="color: #E65100;">Payment Reminder</h2><p>Dear customer,</p><p>This is a friendly reminder that invoice <strong>{{InvoiceNumber}}</strong> is overdue.</p><p><strong>Total:</strong> {{TotalWithVat}} {{CurrencyCode}}</p><p><strong>Due date:</strong> {{DueDate}}</p><p>Please arrange payment at your earliest convenience.</p><br/><p>Thank you.</p><hr style="border: none; border-top: 1px solid #eee; margin: 20px 0;" /><p style="color: #999; font-size: 12px;">{{CompanyName}}</p></div>""";

    /// <summary>Numeric <c>EContentTemplateType</c> values are stored as integers; frozen here like the texts.</summary>
    private const int InvoiceEmail = 10, CreditNoteEmail = 11, AdvanceInvoiceEmail = 12,
        TaxReceiptForAdvanceEmail = 13, InvitationEmail = 20, ReminderEmail = 21;

    /// <summary>(type, original English text, new Czech text) for the four templates that were wrongly English.</summary>
    private static readonly (int Type, string OldSubject, string OldBody, string NewSubject, string NewBody)[] Localized =
    [
        (InvoiceEmail, OriginalInvoiceSubject, OriginalInvoiceBody, DefaultEmailTemplates.InvoiceCsSubject, DefaultEmailTemplates.InvoiceCsBody),
        (CreditNoteEmail, OriginalCreditNoteSubject, OriginalCreditNoteBody, DefaultEmailTemplates.CreditNoteCsSubject, DefaultEmailTemplates.CreditNoteCsBody),
        (InvitationEmail, OriginalInvitationSubject, OriginalInvitationBody, DefaultEmailTemplates.InvitationCsSubject, DefaultEmailTemplates.InvitationCsBody),
        (ReminderEmail, OriginalReminderSubject, OriginalReminderBody, DefaultEmailTemplates.ReminderCsSubject, DefaultEmailTemplates.ReminderCsBody),
    ];

    /// <summary>(type, name, description, subject, body) of the English defaults added for every e-mail type.</summary>
    private static readonly (int Type, string Name, string Description, string Subject, string Body)[] EnglishVariants =
    [
        (InvoiceEmail, "Default Invoice Email (EN)", "English email body when sending an invoice to a client whose language is English.", DefaultEmailTemplates.InvoiceEnSubject, DefaultEmailTemplates.InvoiceEnBody),
        (CreditNoteEmail, "Default Credit Note Email (EN)", "English email body when sending a credit note to a client whose language is English.", DefaultEmailTemplates.CreditNoteEnSubject, DefaultEmailTemplates.CreditNoteEnBody),
        (InvitationEmail, "Default Invitation Email (EN)", "English email sent to new users.", DefaultEmailTemplates.InvitationEnSubject, DefaultEmailTemplates.InvitationEnBody),
        (ReminderEmail, "Default Payment Reminder (EN)", "English payment reminder for overdue invoices.", DefaultEmailTemplates.ReminderEnSubject, DefaultEmailTemplates.ReminderEnBody),
        (AdvanceInvoiceEmail, "Default Advance Invoice Email (EN)", "English email sent with an advance invoice. Tells the recipient that it is not a tax document.", DefaultEmailTemplates.AdvanceInvoiceEnSubject, DefaultEmailTemplates.AdvanceInvoiceEnBody),
        (TaxReceiptForAdvanceEmail, "Default Tax Receipt for Advance Email (EN)", "English email sent with a tax receipt for a received advance payment.", DefaultEmailTemplates.TaxReceiptEnSubject, DefaultEmailTemplates.TaxReceiptEnBody),
    ];

    /// <summary>Statements for <c>Up()</c>, in execution order.</summary>
    public static IEnumerable<string> Up()
    {
        // 1. Czech text, only for rows that are still the untouched original seed (a user-edited template never matches).
        foreach (var t in Localized)
        {
            yield return $"""
                UPDATE "ContentTemplate"
                SET "Subject" = {Q(t.NewSubject)}, "HtmlBody" = {Q(t.NewBody)}
                WHERE "TemplateType" = {t.Type} AND "Language" = 'cs'
                  AND "Subject" = {Q(t.OldSubject)} AND "HtmlBody" = {Q(t.OldBody)};
                """;
        }

        // 2. The seed inserted Ids 1–11 explicitly, which does not advance the identity sequence on every provider
        //    path. Make sure the INSERTs below cannot draw an Id that already exists (burning one value is harmless).
        yield return """
            DO $$
            DECLARE
                seq text := pg_get_serial_sequence('"ContentTemplate"', 'Id');
                max_id bigint;
            BEGIN
                SELECT COALESCE(MAX("Id"), 0) INTO max_id FROM "ContentTemplate";
                IF nextval(seq) <= max_id THEN
                    PERFORM setval(seq, max_id);
                END IF;
            END $$;
            """;

        // 3. English defaults, no explicit Id, and only for a type that has no English template yet
        //    (re-running the migration or a user-created English template never produces a duplicate).
        foreach (var t in EnglishVariants)
        {
            yield return $"""
                INSERT INTO "ContentTemplate" ("Name", "Subject", "HtmlBody", "TemplateType", "IsDefault", "IsActive", "Description", "Language", "CreatedAt")
                SELECT {Q(t.Name)}, {Q(t.Subject)}, {Q(t.Body)}, {t.Type}, TRUE, TRUE, {Q(t.Description)}, 'en', now()
                WHERE NOT EXISTS (SELECT 1 FROM "ContentTemplate" WHERE "TemplateType" = {t.Type} AND "Language" = 'en');
                """;
        }
    }

    /// <summary>
    /// Statements for <c>Down()</c>: the exact inverse, again touching only rows that still equal what <c>Up()</c> wrote.
    /// Anything a user edited since is left alone.
    /// </summary>
    public static IEnumerable<string> Down()
    {
        foreach (var t in EnglishVariants)
        {
            yield return $"""
                DELETE FROM "ContentTemplate"
                WHERE "TemplateType" = {t.Type} AND "Language" = 'en'
                  AND "Subject" = {Q(t.Subject)} AND "HtmlBody" = {Q(t.Body)};
                """;
        }

        foreach (var t in Localized)
        {
            yield return $"""
                UPDATE "ContentTemplate"
                SET "Subject" = {Q(t.OldSubject)}, "HtmlBody" = {Q(t.OldBody)}
                WHERE "TemplateType" = {t.Type} AND "Language" = 'cs'
                  AND "Subject" = {Q(t.NewSubject)} AND "HtmlBody" = {Q(t.NewBody)};
                """;
        }
    }

    /// <summary>Quotes a value as a PostgreSQL string literal (the texts are constants, never user input).</summary>
    private static string Q(string value) => "'" + value.Replace("'", "''") + "'";
}

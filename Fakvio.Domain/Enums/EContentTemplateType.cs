namespace Fakvio.Domain.Enums;

/// <summary>
/// Types of content templates available in the system.
/// Each type corresponds to a specific output (PDF or email) for a specific workflow.
/// Numbering groups: 1-9 PDF outputs, 10-19 document emails, 20-29 system emails.
/// </summary>
public enum EContentTemplateType
{
    // ========== PDF output templates (1-9) ==========

    /// <summary>
    /// HTML template for rendering invoice PDFs.
    /// Placeholders: {{IssuerName}}, {{ClientName}}, {{DocumentNumber}}, {{InvoiceItems}}, etc.
    /// </summary>
    InvoicePdf = 1,

    /// <summary>
    /// HTML template for rendering credit note PDFs.
    /// Uses the same placeholders as InvoicePdf.
    /// </summary>
    CreditNotePdf = 2,

    /// <summary>
    /// HTML template for rendering reminder (dunning) PDF letters.
    /// Placeholders: {{CompanyName}}, {{ClientName}}, {{InvoiceNumber}}, {{DaysOverdue}},
    /// {{InvoiceAmount}}, {{Fee}}, {{Interest}}, {{TotalDue}}, etc.
    /// </summary>
    ReminderPdf = 3,

    /// <summary>
    /// HTML template for rendering pro-forma / advance invoice PDFs (zálohová faktura).
    /// Uses the same layout placeholders as InvoicePdf plus a note that this is NOT a tax document.
    /// Placeholders: same as InvoicePdf — {{IssuerName}}, {{ClientName}}, {{DocumentNumber}}, etc.
    /// </summary>
    AdvanceInvoicePdf = 4,

    /// <summary>
    /// HTML template for rendering tax-receipt-for-advance PDFs (daňový doklad o přijaté platbě).
    /// Issued after the advance payment of a pro-forma is received; this IS a VAT tax document.
    /// Placeholders: same as InvoicePdf plus {{OriginalDocumentNumber}} for the source pro-forma.
    /// </summary>
    TaxReceiptForAdvancePdf = 5,

    // ========== Document email templates (10-19) ==========

    /// <summary>
    /// Email body sent when delivering an invoice to a client.
    /// Placeholders: {{InvoiceNumber}}, {{CompanyName}}, {{TotalWithVat}}, {{DueDate}}, etc.
    /// </summary>
    InvoiceEmail = 10,

    /// <summary>
    /// Email body sent when delivering a credit note to a client.
    /// Uses the same placeholders as InvoiceEmail.
    /// </summary>
    CreditNoteEmail = 11,

    /// <summary>
    /// Email body sent when delivering a pro-forma / advance invoice to a client.
    /// Placeholders: {{InvoiceNumber}}, {{CompanyName}}, {{TotalWithVat}}, {{DueDate}}, etc.
    /// </summary>
    AdvanceInvoiceEmail = 12,

    /// <summary>
    /// Email body sent when delivering a tax receipt for advance payment to a client
    /// (daňový doklad o přijaté platbě).
    /// Placeholders: same as AdvanceInvoiceEmail.
    /// </summary>
    TaxReceiptForAdvanceEmail = 13,

    // ========== System email templates (20-29) ==========

    /// <summary>
    /// Email sent when inviting a new user to the system.
    /// Placeholders: {{FullName}}, {{InvitationLink}}, {{AppName}}
    /// </summary>
    InvitationEmail = 20,

    /// <summary>
    /// Email sent as a payment reminder for overdue invoices.
    /// Placeholders: {{InvoiceNumber}}, {{TotalWithVat}}, {{CurrencyCode}}, {{DueDate}}
    /// </summary>
    ReminderEmail = 21,

    /// <summary>
    /// Email sent for password reset requests (future use).
    /// Placeholders: {{FullName}}, {{ResetLink}}, {{AppName}}
    /// </summary>
    PasswordResetEmail = 22,

    /// <summary>
    /// Email sent with a 6-digit OTP code for Two-Factor Authentication.
    /// Placeholders: {{FullName}}, {{Code}}, {{ExpirationMinutes}}, {{AppName}}
    /// </summary>
    TwoFactorEmail = 23
}

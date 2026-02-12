namespace InvoiceApi.Application.Service;

/// <summary>
/// Service interface for sending emails, primarily for sending invoices as PDF attachments.
/// Uses SMTP configuration from appsettings.json (SmtpSettings section).
/// </summary>
public interface IEmailService
{
    /// <summary>
    /// Sends the specified invoice as an email with a PDF attachment.
    /// Generates the PDF using IPdfExportService, then sends it via SMTP.
    /// </summary>
    /// <param name="invoiceId">The ID of the invoice to send.</param>
    /// <param name="recipientEmail">The recipient's email address.</param>
    /// <param name="ct">Cancellation token for async operation.</param>
    Task SendInvoiceEmailAsync(long invoiceId, string recipientEmail, CancellationToken ct = default);

    /// <summary>
    /// Sends a generic email with optional attachment.
    /// Useful for sending custom emails outside the invoice workflow.
    /// </summary>
    /// <param name="to">Recipient email address.</param>
    /// <param name="subject">Email subject line.</param>
    /// <param name="htmlBody">HTML body content of the email.</param>
    /// <param name="attachment">Optional PDF or other attachment as byte array.</param>
    /// <param name="attachmentName">Filename for the attachment (e.g. "Invoice_001.pdf").</param>
    /// <param name="ct">Cancellation token for async operation.</param>
    Task SendEmailAsync(string to, string subject, string htmlBody, byte[]? attachment = null,
        string? attachmentName = null, CancellationToken ct = default);

    /// <summary>
    /// Sends an invitation email to a newly created user.
    /// The email contains a link with a unique token where the user can set their password.
    /// </summary>
    /// <param name="email">Recipient's email address.</param>
    /// <param name="fullName">Full name of the invited user (used in the greeting).</param>
    /// <param name="invitationLink">Full URL with the invitation token (e.g. https://app.example.com/set-password?token=xxx).</param>
    /// <param name="ct">Cancellation token for async operation.</param>
    Task SendInvitationEmailAsync(string email, string fullName, string invitationLink, CancellationToken ct = default);
}

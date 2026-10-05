using Fakvio.Contracts.Common;
using Fakvio.Contracts.Dto.Email;
using Fakvio.Application.Common.Helpers;
using Fakvio.Application.Exceptions;
using Fakvio.Application.Service;
using Fakvio.Domain.Entities;
using Fakvio.Domain.Enums;
using Fakvio.Infrastructure.Data;
using Fakvio.Infrastructure.Service.Ubl;
using MailKit.Security;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using MailKit.Net.Smtp;
using MimeKit;

namespace Fakvio.Infrastructure.Service;

/// <summary>
/// Email service implementation using MailKit for SMTP delivery.
///
/// SMTP settings resolution (3-tier priority order):
/// 1. CompanySystemSettings in master DB — per-company SMTP (Admin configures on MyCompany page)
/// 2. SystemConfiguration table in master DB — system-wide SMTP (SysAdmin configures)
/// 3. Fallback to "SmtpSettings" section in appsettings.json (for fresh installs)
///
/// This 3-tier approach allows each company to send emails from their own mail server,
/// while providing a sensible fallback chain for companies that haven't configured SMTP.
///
/// Uses IContentTemplateService for template-based emails with placeholder substitution.
/// Falls back to a simple HTML body if no template is found.
/// </summary>
public class EmailService : IEmailService
{
    private readonly TenantDbContext _context;
    private readonly MasterDbContext _masterContext;
    private readonly IPdfExportService _pdfExportService;
    private readonly IDocumentFileNameService _fileNameService;
    private readonly IFileAttachmentService _fileAttachmentService;
    private readonly IIsdocExportService _isdocExportService;
    private readonly IUblExportService _ublExportService;
    private readonly IContentTemplateService _contentTemplateService;
    private readonly ISystemConfigurationService _systemConfigService;
    private readonly ICredentialProtector _credentialProtector;
    private readonly ITenantResolver _tenantResolver;
    private readonly IConfiguration _configuration;
    private readonly ILogger<EmailService> _logger;
    private readonly IWebhookPublisher? _webhookPublisher;

    /// <summary>
    /// Internal record to hold resolved SMTP configuration from any tier.
    /// This avoids passing 7+ parameters around — a single object holds everything needed
    /// to connect to an SMTP server and send an email.
    /// </summary>
    internal record SmtpSettings(
        string Host,
        int Port,
        string? Username,
        string? Password,
        string SenderEmail,
        string SenderName,
        bool UseSsl);

    public EmailService(
        TenantDbContext context,
        MasterDbContext masterContext,
        IPdfExportService pdfExportService,
        IDocumentFileNameService fileNameService,
        IFileAttachmentService fileAttachmentService,
        IIsdocExportService isdocExportService,
        IUblExportService ublExportService,
        IContentTemplateService contentTemplateService,
        ISystemConfigurationService systemConfigService,
        ICredentialProtector credentialProtector,
        ITenantResolver tenantResolver,
        IConfiguration configuration,
        ILogger<EmailService> logger,
        IWebhookPublisher? webhookPublisher = null)
    {
        _context = context;
        _masterContext = masterContext;
        _pdfExportService = pdfExportService;
        _fileNameService = fileNameService;
        _fileAttachmentService = fileAttachmentService;
        _isdocExportService = isdocExportService;
        _ublExportService = ublExportService;
        _contentTemplateService = contentTemplateService;
        _systemConfigService = systemConfigService;
        _credentialProtector = credentialProtector;
        _tenantResolver = tenantResolver;
        _configuration = configuration;
        _logger = logger;
        _webhookPublisher = webhookPublisher;
    }

    /// <inheritdoc />
    public async Task SendInvoiceEmailAsync(long invoiceId, string recipientEmail, CancellationToken ct = default,
        SendInvoiceEmailOptions? options = null)
    {
        // Cheap validation first - a bad CC list must reject the request before any PDF is generated.
        var ccEmails = EmailAddressList.Normalize(options?.CcEmails);
        if (EmailAddressList.ValidateCc(ccEmails) is { } ccError)
            throw new ArgumentException(ccError);

        // Tracked (not AsNoTracking): "save as client default" below edits Client.BillingSettings.
        var invoice = await LoadInvoiceForEmailAsync(invoiceId, track: true, ct)
            ?? throw new KeyNotFoundException($"Invoice with ID {invoiceId} not found.");

        // Blank recipient = the client's default e-mail contact (this is also what bulk send relies on).
        var to = string.IsNullOrWhiteSpace(recipientEmail)
            ? OrderEmailContacts(invoice.Client?.Contact).FirstOrDefault()?.ContactValue.Trim()
            : recipientEmail.Trim();
        if (string.IsNullOrEmpty(to))
            throw new InvalidOperationException(
                "No recipient: pass a recipient address or add an e-mail contact to the client.");

        _logger.LogInformation("Sending invoice {InvoiceId} via email to {Email}", invoiceId, to);

        // CC must never repeat the To address (the recipient would get two copies).
        ccEmails.RemoveAll(cc => string.Equals(cc, to, StringComparison.OrdinalIgnoreCase));

        // Which generated files go out: the sender's explicit choice wins, otherwise the client's
        // default (client → company → system default).
        var (wantPdf, wantIsdoc, wantUbl, resolved, ublEffectiveDefault) = ResolveAttachmentPlan(invoice, options);

        // Stored invoice files the user ticked. Loaded (and ownership + size checked) BEFORE the expensive
        // PDF generation, so a bad id or an oversized selection fails fast.
        var storedFiles = options?.FileAttachmentIds is { Count: > 0 } storedIds
            ? await _fileAttachmentService.DownloadSelectedAsync(
                "Invoice", invoiceId, storedIds, EmailAttachmentValidator.MaxEmailTotalBytes, ct)
            : [];

        // Generate the PDF attachment using the PDF export service.
        byte[]? pdfBytes = null;
        string? pdfFileName = null;
        if (wantPdf)
        {
            pdfBytes = await _pdfExportService.GenerateInvoicePdfAsync(invoiceId, ct);
            // Attachment names come from the configured per-client / per-document-type file name format,
            // so they match what the user gets when downloading the same document.
            pdfFileName = await _fileNameService.GetFileNameAsync(invoiceId, DocumentFileName.PdfExtension, ct);
        }

        // Generate the ISDOC XML attachment.
        // ISDOC 6.0.2 is the Czech electronic invoice standard read by Pohoda, Money, Helios.
        byte[]? isdocBytes = null;
        string? isdocFileName = null;
        if (wantIsdoc)
        {
            isdocBytes = await _isdocExportService.ExportInvoiceAsync(invoiceId, ct);
            isdocFileName = await _fileNameService.GetFileNameAsync(invoiceId, DocumentFileName.IsdocExtension, ct);
        }

        // UBL/Peppol attachment (ADR 0002, F1.9). By default (nothing configured, nothing requested) it is
        // only built for a buyer who is actually reachable via Peppol: a Slovak buyer (SK e-invoicing 2027)
        // or any buyer with an explicit Client.PeppolId override (F1.8) - see ResolveAttachmentPlan.
        // A pre-flight failure (missing Peppol ID, incomplete address, …) just skips the attachment - the
        // email itself must never fail because the UBL side of the document is not ready yet; PDF + ISDOC
        // still go out as before. The one exception is an EXPLICIT request (options.AttachUbl == true):
        // there the sender must not be told "sent" while the file they asked for is missing.
        // Sending e-invoice XML by email instead of through a Peppol courier is only lawful with
        // the recipient's consent ([FAQ] I/66) - USERGUIDE §2.8a tells the user that.
        byte[]? ublBytes = null;
        string? ublFileName = null;
        if (wantUbl)
        {
            try
            {
                ublBytes = await _ublExportService.ExportInvoiceAsync(invoiceId, ct);
                ublFileName = await _fileNameService.GetFileNameAsync(invoiceId, DocumentFileName.UblExtension, ct);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw; // A caller-requested cancellation must still abort the whole send.
            }
            catch (Exception ex) when (options?.AttachUbl == true)
            {
                _logger.LogWarning(ex, "Explicitly requested UBL could not be generated for invoice {InvoiceId}: {Message}",
                    invoiceId, ex.Message);
                throw new InvalidOperationException($"The UBL attachment could not be generated: {ex.Message}", ex);
            }
            catch (Exception ex)
            {
                // Any UBL failure — not just TenantNotReadyException — must leave the email
                // unaffected: a mapper bug, a transient DB error, or an unexpected document
                // type must not cost the recipient their PDF + ISDOC because a comfort
                // attachment could not be built.
                _logger.LogWarning(ex,
                    "UBL attachment skipped for invoice {InvoiceId} email — export failed: {Message}",
                    invoiceId, ex.Message);
            }
        }

        // Extra files (API/MCP base64 upload, already validated at the API boundary) followed by the stored
        // invoice files. Names are made unique (case-insensitive, "name (2).ext") across ALL attachments,
        // because two files with one name overwrite each other when the recipient saves them together.
        // Only names and sizes are ever logged - never the content.
        var usedNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var generatedName in new[] { pdfFileName, isdocFileName, ublFileName })
        {
            if (generatedName != null)
                usedNames.Add(generatedName);
        }

        var extraAttachments = new List<OutgoingEmailAttachment>();
        foreach (var extra in options?.Attachments ?? [])
            extraAttachments.Add(extra with { FileName = ZipArchiveHelper.UniqueEntryName(usedNames, extra.FileName) });
        foreach (var (content, meta) in storedFiles)
        {
            // Path.GetFileName: a stored name is user data, never let it carry a path into the MIME part.
            var storedName = Path.GetFileName(meta.OriginalFileName);
            extraAttachments.Add(new OutgoingEmailAttachment(
                ZipArchiveHelper.UniqueEntryName(usedNames, string.IsNullOrWhiteSpace(storedName) ? "attachment" : storedName),
                content));
        }

        // Final size guard on the real bytes (the stored-file check above only knew the metadata).
        var totalBytes = (long)(pdfBytes?.Length ?? 0) + (isdocBytes?.Length ?? 0) + (ublBytes?.Length ?? 0)
                         + extraAttachments.Sum(a => (long)a.Content.Length);
        if (totalBytes > EmailAttachmentValidator.MaxEmailTotalBytes)
            throw new InvalidOperationException(
                $"The attachments are too large ({totalBytes / (1024 * 1024)} MB); the limit for one e-mail is " +
                $"{EmailAttachmentValidator.MaxEmailTotalBytes / (1024 * 1024)} MB.");

        _logger.LogInformation(
            "Invoice {InvoiceId} attachments prepared: PDF={PdfInfo}, ISDOC={IsdocInfo}{UblInfo}, extra={ExtraCount} [{ExtraNames}], cc={CcCount}",
            invoiceId,
            pdfBytes != null ? $"{pdfFileName} ({pdfBytes.Length} B)" : "(not attached)",
            isdocBytes != null ? $"{isdocFileName} ({isdocBytes.Length} B)" : "(not attached)",
            ublBytes != null ? $", UBL={ublFileName} ({ublBytes.Length} B)" : "",
            extraAttachments.Count,
            string.Join(", ", extraAttachments.Select(a => $"{a.FileName} ({a.Content.Length} B)")),
            ccEmails.Count);

        // Try to render from the default content template for the client's language, fall back to simple HTML
        var (templateSubject, templateBody, placeholders) = await RenderInvoiceTemplateAsync(invoice, ct);

        // Let the caller's custom subject / message / full body override or extend the template output.
        var (subject, htmlBody) = ApplyCustomization(templateSubject, templateBody, options, placeholders);

        // Build and send the email with the chosen generated files (+ UBL when applicable, F1.9) + extra files.
        // We call the internal builder directly instead of SendEmailAsync because
        // the public SendEmailAsync signature supports only one optional attachment
        // and we don't want to break that public interface (per issue #16 analysis).
        await SendInvoiceMessageAsync(
            to, subject, htmlBody,
            pdfBytes, pdfFileName,
            isdocBytes, isdocFileName,
            ublBytes, ublFileName,
            ct,
            extraAttachments,
            ccEmails);

        // Mark the invoice as sent by email in the database
        invoice.IsSentByEmail = true;
        invoice.LastSentByEmailAt = DateTime.UtcNow;

        // "Save as default for this client": only after the mail really left, so a failed send changes nothing.
        // Persisted in the same SaveChanges as the "sent" flag.
        if (options is { SaveAttachmentDefaults: true } && invoice.Client != null)
        {
            invoice.Client.BillingSettings ??= new BillingSettings { ClientId = invoice.Client.Id, CreatedAt = DateTime.UtcNow };
            EmailAttachmentDefaults.ApplyChoices(
                invoice.Client.BillingSettings, resolved, ublEffectiveDefault,
                options.AttachPdf, options.AttachIsdoc, options.AttachUbl);
            _logger.LogInformation("Saved e-mail attachment defaults for client {ClientId}", invoice.Client.Id);
        }

        await _context.SaveChangesAsync(ct);

        // "invoice.sent" webhook — never throws (see IWebhookPublisher).
        if (_webhookPublisher != null)
            await _webhookPublisher.PublishInvoiceEventAsync(Contracts.Dto.Webhook.WebhookEventCatalog.InvoiceSent, invoiceId, ct);

        var sentNames = new List<string>();
        if (pdfFileName != null)
            sentNames.Add(pdfFileName);
        if (isdocFileName != null)
            sentNames.Add(isdocFileName);
        if (ublBytes != null)
            sentNames.Add(ublFileName!);
        sentNames.AddRange(extraAttachments.Select(a => a.FileName));
        var attachmentCount = sentNames.Count;
        var attachmentNames = string.Join(", ", sentNames);
        _logger.LogInformation(
            "Invoice {InvoiceId} email sent successfully to {Email} with {AttachmentCount} attachments: {Attachments}",
            invoiceId, to, attachmentCount, attachmentNames);
    }

    /// <inheritdoc />
    public async Task<InvoiceEmailDefaultsDto> GetSendDefaultsAsync(long invoiceId, CancellationToken ct = default)
    {
        var invoice = await LoadInvoiceForEmailAsync(invoiceId, track: false, ct)
            ?? throw new KeyNotFoundException($"Invoice with ID {invoiceId} not found.");

        var resolved = EmailAttachmentDefaults.Resolve(invoice.Client?.BillingSettings, invoice.Issuer?.BillingSettings);
        var autoWouldAttach = IsPeppolReachable(invoice);

        var defaults = new InvoiceEmailDefaultsDto
        {
            Recipients = OrderEmailContacts(invoice.Client?.Contact)
                .Select(c => new EmailRecipientOptionDto
                {
                    Email = c.ContactValue.Trim(),
                    Label = c.Label,
                    IsPrimary = c.IsPrimary,
                    IsInvoiceEmail = c.ContactType == EContactType.InvoiceEmail
                })
                .ToList(),
            AttachPdf = resolved.Pdf,
            AttachIsdoc = resolved.Isdoc,
            AttachUbl = resolved.Ubl ?? autoWouldAttach,
            UblIsAutomatic = resolved.Ubl is null,
            UblAutoWouldAttach = autoWouldAttach,
            StoredAttachments = await _fileAttachmentService.GetByEntityAsync("Invoice", invoiceId, ct)
        };

        // The dialog prefills the subject AND the body from ONE render, so what the user reads is exactly what
        // the template produces. If the template cannot be rendered the dialog still works (the server falls
        // back to the template at send time when no custom subject / body is sent).
        try
        {
            var rendered = await RenderInvoiceTemplateAsync(invoice, ct);
            defaults.DefaultSubject = rendered.Subject;
            defaults.DefaultHtmlBody = rendered.HtmlBody;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "Default e-mail subject / body for invoice {InvoiceId} could not be rendered", invoiceId);
        }

        return defaults;
    }

    /// <summary>
    /// Loads an invoice with everything the e-mail needs: currency and issuer for the template, the client's
    /// address (buyer country → UBL rule), contacts (default recipient) and BOTH BillingSettings (client + issuer
    /// → attachment defaults). Split query because Address and Contact are two collections: a single query would
    /// multiply their rows (cartesian explosion).
    /// </summary>
    private async Task<Invoice?> LoadInvoiceForEmailAsync(long invoiceId, bool track, CancellationToken ct)
    {
        var query = _context.Invoice
            .Include(i => i.Currency)
            .Include(i => i.Issuer)
                .ThenInclude(c => c.BillingSettings)
            .Include(i => i.Client)
                .ThenInclude(c => c!.Address)
            .Include(i => i.Client)
                .ThenInclude(c => c!.Contact)
            .Include(i => i.Client)
                .ThenInclude(c => c!.BillingSettings)
            .AsSplitQuery();

        if (!track)
            query = query.AsNoTracking();

        return await query.FirstOrDefaultAsync(i => i.Id == invoiceId, ct);
    }

    /// <summary>
    /// A buyer who is actually reachable via Peppol: a Slovak buyer (SK e-invoicing 2027) or any buyer with
    /// an explicit Client.PeppolId override (F1.8). This is the "automatic" rule for the UBL attachment.
    /// </summary>
    private static bool IsPeppolReachable(Invoice invoice)
    {
        var buyerCountry = UblCodes.CountryToIso2(
            invoice.Client?.Address?.FirstOrDefault(a => a.IsPrimary)?.Country
                ?? invoice.Client?.Address?.FirstOrDefault()?.Country);
        return buyerCountry == "SK" || !string.IsNullOrWhiteSpace(invoice.Client?.PeppolId);
    }

    /// <summary>
    /// Decides which generated files to build: the explicit per-send choice, else the resolved client default.
    /// Also returns the resolved defaults and the effective UBL default so "save as default" can tell a real
    /// change from a mere confirmation of what was already preselected.
    /// </summary>
    private static (bool Pdf, bool Isdoc, bool Ubl, ResolvedEmailAttachments Resolved, bool UblEffectiveDefault)
        ResolveAttachmentPlan(Invoice invoice, SendInvoiceEmailOptions? options)
    {
        var resolved = EmailAttachmentDefaults.Resolve(invoice.Client?.BillingSettings, invoice.Issuer?.BillingSettings);
        var ublEffectiveDefault = resolved.Ubl ?? IsPeppolReachable(invoice);
        return (
            options?.AttachPdf ?? resolved.Pdf,
            options?.AttachIsdoc ?? resolved.Isdoc,
            options?.AttachUbl ?? ublEffectiveDefault,
            resolved,
            ublEffectiveDefault);
    }

    /// <summary>
    /// The client's e-mail contacts, best recipient first: the dedicated "invoice e-mail" type before a general
    /// e-mail, primary before non-primary, then the oldest. Blank values and duplicates are dropped.
    /// </summary>
    internal static List<Contact> OrderEmailContacts(IEnumerable<Contact>? contacts) =>
        (contacts ?? [])
            .Where(c => c.ContactType is EContactType.InvoiceEmail or EContactType.Email
                        && !string.IsNullOrWhiteSpace(c.ContactValue))
            .OrderByDescending(c => c.ContactType == EContactType.InvoiceEmail)
            .ThenByDescending(c => c.IsPrimary)
            .ThenBy(c => c.Id)
            .DistinctBy(c => c.ContactValue.Trim(), StringComparer.OrdinalIgnoreCase)
            .ToList();

    /// <summary>
    /// Renders the invoice e-mail template (subject + HTML body) for the client's language, falling back to
    /// a simple built-in text. Shared by sending and by the dialog's "default subject / body" so both always agree.
    /// Also returns the placeholder values, so a caller-supplied subject / body can have its own
    /// <c>{{Placeholders}}</c> filled in with the same data.
    /// </summary>
    private async Task<(string Subject, string HtmlBody, Dictionary<string, string> Placeholders)> RenderInvoiceTemplateAsync(
        Invoice invoice, CancellationToken ct)
    {
        // Build placeholders dictionary for template substitution
        var placeholders = new Dictionary<string, string>
        {
            ["InvoiceNumber"] = invoice.DocumentNumber ?? invoice.Id.ToString(),
            ["CompanyName"] = invoice.Issuer?.CompanyName ?? "",
            ["TotalWithVat"] = invoice.TotalWithVat.ToString("N2"),
            ["CurrencyCode"] = invoice.Currency?.Code ?? "",
            ["DueDate"] = invoice.DueDate?.ToString("dd.MM.yyyy") ?? "N/A",
            ["IssueDate"] = invoice.IssueDate?.ToString("dd.MM.yyyy") ?? "N/A",
            ["AppName"] = await ResolveAppNameAsync(ct)
        };

        // Determine email template type based on document type.
        // The mapping is centralised in PdfExportService so it is always consistent
        // between PDF and email template resolution.
        var templateType = PdfExportService.ResolveEmailTemplateType(invoice.DocumentType);

        // Read client's preferred language for template resolution.
        // Falls back to "cs" (Czech) when client or language is not set.
        var clientLanguage = invoice.Client?.Language ?? "cs";

        var (subject, htmlBody) = await RenderFromTemplateOrFallbackAsync(templateType, placeholders, clientLanguage, ct);
        return (subject, htmlBody, placeholders);
    }

    /// <summary>
    /// Builds and sends a MimeMessage with PDF + ISDOC, and optionally a UBL/Peppol XML
    /// attachment (F1.9). This is an internal helper used only by
    /// <see cref="SendInvoiceEmailAsync"/>. Keeping it separate avoids changing the public
    /// <see cref="SendEmailAsync"/> signature which only supports a single optional attachment.
    /// </summary>
    /// <param name="to">Recipient email address.</param>
    /// <param name="subject">Email subject.</param>
    /// <param name="htmlBody">HTML email body.</param>
    /// <param name="pdfBytes">Raw PDF bytes to attach.</param>
    /// <param name="pdfFileName">Filename for the PDF attachment (e.g. "Invoice_2025001.pdf").</param>
    /// <param name="isdocBytes">Raw ISDOC XML bytes to attach.</param>
    /// <param name="isdocFileName">Filename for the ISDOC attachment (e.g. "Invoice_2025001.isdoc").</param>
    /// <param name="ublBytes">Raw UBL XML bytes to attach, or null to skip the attachment.</param>
    /// <param name="ublFileName">Filename for the UBL attachment; required when <paramref name="ublBytes"/> is set.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <param name="extraAttachments">Caller-supplied extra files appended after PDF / ISDOC / UBL, or null for none.</param>
    /// <param name="ccEmails">CC recipients, or null for none.</param>
    internal async Task SendInvoiceMessageAsync(
        string to, string subject, string htmlBody,
        byte[]? pdfBytes, string? pdfFileName,
        byte[]? isdocBytes, string? isdocFileName,
        byte[]? ublBytes = null, string? ublFileName = null,
        CancellationToken ct = default,
        IReadOnlyList<OutgoingEmailAttachment>? extraAttachments = null,
        IReadOnlyList<string>? ccEmails = null)
    {
        // Resolve SMTP settings using the 3-tier priority chain
        var smtp = await ResolveSmtpSettingsAsync(ct);

        // Build the MimeMessage with HTML body + attachments
        var message = BuildInvoiceMessage(
            smtp.SenderName, smtp.SenderEmail,
            to, subject, htmlBody,
            pdfBytes, pdfFileName,
            isdocBytes, isdocFileName,
            ublBytes, ublFileName,
            extraAttachments,
            ccEmails);

        // Connect and send using MailKit
        await SendViaSMTPAsync(smtp, message, ct);
    }

    /// <summary>
    /// Builds a MimeMessage for an invoice email containing PDF + ISDOC attachments, and
    /// optionally a third UBL/Peppol XML attachment (F1.9, e.g. for an SK buyer). Extracted as
    /// a separate static method so unit tests can verify the message structure without a real
    /// SMTP server — call this directly and inspect the returned MimeMessage.
    /// </summary>
    /// <param name="senderName">Display name shown in the "From" header (e.g. "Fakvio").</param>
    /// <param name="senderEmail">Email address in the "From" header.</param>
    /// <param name="to">Recipient email address.</param>
    /// <param name="subject">Email subject line.</param>
    /// <param name="htmlBody">HTML body of the email.</param>
    /// <param name="pdfBytes">Raw PDF bytes to attach.</param>
    /// <param name="pdfFileName">Filename for the PDF (e.g. "Invoice_INV2025001.pdf").</param>
    /// <param name="isdocBytes">Raw ISDOC XML bytes to attach.</param>
    /// <param name="isdocFileName">Filename for the ISDOC (e.g. "Invoice_INV2025001.isdoc").</param>
    /// <param name="ublBytes">Raw UBL XML bytes to attach, or null to skip the attachment.</param>
    /// <param name="ublFileName">Filename for the UBL attachment; required when <paramref name="ublBytes"/> is set.</param>
    /// <param name="extraAttachments">Caller-supplied extra files appended after PDF / ISDOC / UBL, or null for none.</param>
    /// <param name="ccEmails">CC recipients (bare addresses), or null for none.</param>
    /// <remarks>
    /// <paramref name="pdfBytes"/> / <paramref name="isdocBytes"/> are null when the sender (or the client's default)
    /// switched that attachment off.
    /// </remarks>
    internal static MimeMessage BuildInvoiceMessage(
        string senderName, string senderEmail,
        string to, string subject, string htmlBody,
        byte[]? pdfBytes, string? pdfFileName,
        byte[]? isdocBytes, string? isdocFileName,
        byte[]? ublBytes = null, string? ublFileName = null,
        IReadOnlyList<OutgoingEmailAttachment>? extraAttachments = null,
        IReadOnlyList<string>? ccEmails = null)
    {
        var message = new MimeMessage();
        message.From.Add(new MailboxAddress(senderName, senderEmail));
        message.To.Add(MailboxAddress.Parse(to));
        foreach (var cc in ccEmails ?? [])
        {
            // The API boundary already validated the list; this is the last line of defence for other callers.
            if (!MailboxAddress.TryParse(cc, out var ccAddress))
                throw new ArgumentException($"CC address '{cc}' is not a valid e-mail address.");
            message.Cc.Add(ccAddress);
        }
        message.Subject = subject;

        var bodyBuilder = new BodyBuilder { HtmlBody = htmlBody };

        // PDF invoice — standard document type
        if (pdfBytes != null && pdfFileName != null)
            bodyBuilder.Attachments.Add(pdfFileName, pdfBytes, ContentType.Parse("application/pdf"));

        // ISDOC XML — Czech electronic invoice standard (ISO/IEC 19845 based).
        // Content type "application/xml" is the MIME type for XML documents (RFC 7303).
        if (isdocBytes != null && isdocFileName != null)
            bodyBuilder.Attachments.Add(isdocFileName, isdocBytes, ContentType.Parse("application/xml"));

        // UBL/Peppol XML (F1.9) — only when the caller resolved one (SK buyer or explicit
        // Client.PeppolId override, and the pre-flight actually passed).
        if (ublBytes != null && ublFileName != null)
            bodyBuilder.Attachments.Add(ublFileName, ublBytes, ContentType.Parse("application/xml"));

        // Caller-supplied extra files. The content type is derived from the file extension by
        // MimeKit (the extension was whitelisted by EmailAttachmentValidator) - we never trust a
        // client-supplied MIME type.
        if (extraAttachments != null)
        {
            foreach (var extra in extraAttachments)
                bodyBuilder.Attachments.Add(extra.FileName, extra.Content, ContentType.Parse(MimeTypes.GetMimeType(extra.FileName)));
        }

        message.Body = bodyBuilder.ToMessageBody();
        return message;
    }

    /// <summary>
    /// Applies the caller's optional subject / message / full body on top of the rendered template.
    /// Extracted as a static method so it can be unit-tested without SMTP or a database.
    /// </summary>
    /// <remarks>
    /// Junior note on the message: it is plain text from an untrusted caller, so it is HTML-encoded
    /// (a "&lt;script&gt;" becomes harmless text) and only then are line breaks turned into
    /// &lt;br&gt; - the one piece of HTML we add ourselves.
    /// <para>
    /// A <see cref="SendInvoiceEmailOptions.HtmlBody"/> is different: it is deliberate HTML (the user edited the
    /// whole e-mail in the dialog), so it replaces the template body and the <c>Message</c> is ignored.
    /// The API boundary has already rejected scripts and oversized bodies (<c>EmailContentValidator</c>).
    /// </para>
    /// <para>
    /// Subject and body overrides get their <c>{{Placeholders}}</c> filled in: harmless when the text was already
    /// rendered (nothing is left to replace) and it lets a user or an AI type <c>{{InvoiceNumber}}</c>.
    /// </para>
    /// </remarks>
    internal static (string Subject, string HtmlBody) ApplyCustomization(
        string templateSubject, string templateBody, SendInvoiceEmailOptions? options,
        IReadOnlyDictionary<string, string>? placeholders = null)
    {
        var subject = templateSubject;
        var htmlBody = templateBody;

        if (!string.IsNullOrWhiteSpace(options?.Subject))
        {
            // CR/LF in a subject would let a caller inject extra mail headers.
            subject = ContentTemplateService.ReplacePlaceholders(options.Subject, placeholders)
                .Replace("\r", " ").Replace("\n", " ").Trim();
        }

        if (!string.IsNullOrWhiteSpace(options?.HtmlBody))
        {
            // Full body override: replaces the template, so a separate Message intro would be confusing.
            htmlBody = ContentTemplateService.ReplacePlaceholders(options.HtmlBody, placeholders);
        }
        else if (!string.IsNullOrWhiteSpace(options?.Message))
        {
            var encoded = System.Net.WebUtility.HtmlEncode(options.Message.Trim())
                .Replace("\r\n", "\n").Replace("\r", "\n").Replace("\n", "<br>");
            htmlBody = $"<p>{encoded}</p>{templateBody}";
        }

        return (subject, htmlBody);
    }

    /// <inheritdoc />
    public async Task SendEmailAsync(string to, string subject, string htmlBody, byte[]? attachment = null,
        string? attachmentName = null, CancellationToken ct = default)
    {
        // Resolve SMTP settings using the 3-tier priority chain:
        // Company SMTP → System SMTP → appsettings.json
        var smtp = await ResolveSmtpSettingsAsync(ct);

        _logger.LogInformation("Sending email to {To}, subject: {Subject}, via {Host}:{Port}",
            to, subject, smtp.Host, smtp.Port);

        // Build the MimeMessage (email) using MimeKit
        var message = new MimeMessage();
        message.From.Add(new MailboxAddress(smtp.SenderName, smtp.SenderEmail));
        message.To.Add(MailboxAddress.Parse(to));
        message.Subject = subject;

        // Use a BodyBuilder to construct the email body with optional attachment
        var bodyBuilder = new BodyBuilder
        {
            HtmlBody = htmlBody
        };

        // If an attachment is provided (e.g. PDF invoice), add it to the email
        if (attachment != null && !string.IsNullOrEmpty(attachmentName))
        {
            bodyBuilder.Attachments.Add(attachmentName, attachment, ContentType.Parse("application/pdf"));
        }

        message.Body = bodyBuilder.ToMessageBody();

        await SendViaSMTPAsync(smtp, message, ct);

        _logger.LogInformation("Email sent successfully to {To}", to);
    }

    /// <summary>
    /// Connects to the SMTP server and sends the given MimeMessage.
    /// Extracted from SendEmailAsync so it can be reused by SendInvoiceMessageAsync
    /// without duplicating the TLS / auth / SASL setup code.
    ///
    /// Connect logic:
    ///   - Port 465 → SslOnConnect (implicit TLS, full encryption from byte one)
    ///   - Port 587 → StartTls (plain connect then STARTTLS upgrade — most common)
    ///   - UseSsl=false → None (no encryption, for trusted internal relay servers)
    /// </summary>
    /// <remarks>
    /// <c>internal virtual</c> is the test seam: unit / integration tests derive from <see cref="EmailService"/> and
    /// capture the finished <see cref="MimeMessage"/> here instead of opening a real SMTP connection.
    /// </remarks>
    internal virtual async Task SendViaSMTPAsync(SmtpSettings smtp, MimeMessage message, CancellationToken ct)
    {
        var socketOptions = ResolveSocketOptions(smtp.Port, smtp.UseSsl);
        _logger.LogInformation("Connecting to SMTP {Host}:{Port} with {Options}",
            smtp.Host, smtp.Port, socketOptions);

        using var smtpClient = new SmtpClient();

        // Accept server certificates even when CRL (Certificate Revocation List) endpoints
        // are unreachable. Many corporate/self-hosted SMTP servers use certificates whose
        // CRL distribution points are internal or offline — MailKit's default validation
        // rejects these with "unable to get certificate CRL". We still validate the chain;
        // we only relax the revocation check, which matches how most email clients behave.
        smtpClient.ServerCertificateValidationCallback = (sender, certificate, chain, sslPolicyErrors) =>
        {
            // No errors at all — fully trusted certificate
            if (sslPolicyErrors == System.Net.Security.SslPolicyErrors.None)
                return true;

            // If the only issue is the remote certificate chain (e.g. CRL unreachable),
            // check whether every chain status is a revocation-related status we can tolerate.
            if (sslPolicyErrors == System.Net.Security.SslPolicyErrors.RemoteCertificateChainErrors
                && chain != null)
            {
                foreach (var chainElement in chain.ChainElements)
                {
                    foreach (var status in chainElement.ChainElementStatus)
                    {
                        // Skip "no error" entries
                        if (status.Status == System.Security.Cryptography.X509Certificates.X509ChainStatusFlags.NoError)
                            continue;

                        // Tolerate revocation-related flags (CRL offline, revocation undetermined)
                        if (status.Status == System.Security.Cryptography.X509Certificates.X509ChainStatusFlags.RevocationStatusUnknown
                            || status.Status == System.Security.Cryptography.X509Certificates.X509ChainStatusFlags.OfflineRevocation)
                            continue;

                        // Any other chain error (expired, untrusted root, name mismatch) → reject
                        _logger.LogWarning(
                            "SMTP TLS certificate chain error: {Status} — {Info}",
                            status.Status, status.StatusInformation);
                        return false;
                    }
                }

                // All chain statuses were revocation-related — accept the certificate
                _logger.LogDebug(
                    "SMTP TLS certificate accepted despite CRL unavailability for {Host}:{Port}",
                    smtp.Host, smtp.Port);
                return true;
            }

            // Other SSL errors (name mismatch, untrusted root without chain detail) → reject
            _logger.LogWarning("SMTP TLS rejected: SslPolicyErrors={Errors} for {Host}:{Port}",
                sslPolicyErrors, smtp.Host, smtp.Port);
            return false;
        };

        await smtpClient.ConnectAsync(smtp.Host, smtp.Port, socketOptions, ct);

        // Remove SASL mechanisms that cause "535 5.7.8 incorrect credentials" on providers
        // like Seznam.cz when running in Azure Functions. MailKit negotiates the auth mechanism
        // with the server, and in cloud environments it may attempt XOAUTH2 or NTLM before
        // falling back to PLAIN/LOGIN. Seznam.cz doesn't support these, so remove them
        // to force PLAIN or LOGIN authentication which is what the server actually expects.
        smtpClient.AuthenticationMechanisms.Remove("XOAUTH2");
        smtpClient.AuthenticationMechanisms.Remove("NTLM");

        // Authenticate if credentials are provided.
        // Wrapped in try-catch to log the exact SMTP server error before re-throwing,
        // as MailKit's AuthenticationException message can be generic.
        if (!string.IsNullOrEmpty(smtp.Username) && !string.IsNullOrEmpty(smtp.Password))
        {
            try
            {
                // Use explicit UTF-8 encoding — Azure environment variable handling can
                // mangle non-ASCII characters in passwords, and some SMTP servers require
                // UTF-8 encoding for the SASL PLAIN mechanism.
                await smtpClient.AuthenticateAsync(
                    System.Text.Encoding.UTF8, smtp.Username, smtp.Password, ct);
            }
            catch (AuthenticationException ex)
            {
                _logger.LogError(ex,
                    "SMTP authentication failed for user '{Username}' on {Host}:{Port} ({Options}). " +
                    "Verify credentials and that the server supports the selected security option.",
                    smtp.Username, smtp.Host, smtp.Port, socketOptions);
                throw;
            }
        }

        await smtpClient.SendAsync(message, ct);
        await smtpClient.DisconnectAsync(true, ct);
    }

    /// <inheritdoc />
    /// <summary>
    /// Sends an invitation email to a newly created user.
    /// Uses the InvitationEmail template if available, otherwise falls back to default HTML.
    /// </summary>
    public async Task SendInvitationEmailAsync(string email, string fullName, string invitationLink, CancellationToken ct = default)
    {
        _logger.LogInformation("Sending invitation email to {Email} ({FullName})", email, fullName);

        var placeholders = new Dictionary<string, string>
        {
            ["FullName"] = fullName,
            ["InvitationLink"] = invitationLink,
            ["AppName"] = await ResolveAppNameAsync(ct)
        };

        // System emails (invitation, 2FA) have no client context — use "cs" as default language.
        var (subject, htmlBody) = await RenderFromTemplateOrFallbackAsync(
            EContentTemplateType.InvitationEmail, placeholders, "cs", ct);

        // Use the generic SendEmailAsync — no attachment for invitation emails
        await SendEmailAsync(email, subject, htmlBody, ct: ct);

        _logger.LogInformation("Invitation email sent successfully to {Email}", email);
    }

    /// <summary>
    /// Resolves SMTP settings using a 3-tier fallback chain.
    ///
    /// Tier 1: Company-specific SMTP from CompanySystemSettings (master DB).
    ///         Only used when the current request has a CompanyId AND that company
    ///         has configured SMTP (SmtpHost is non-empty).
    ///
    /// Tier 2: System-wide SMTP from SystemConfiguration table (master DB).
    ///         Used when company SMTP is not configured. SysAdmin manages this.
    ///
    /// Tier 3: Fallback to appsettings.json "SmtpSettings" section.
    ///         For fresh installations before any DB configuration is set up.
    ///
    /// Throws InvalidOperationException if no SMTP host is configured anywhere.
    /// </summary>
    private async Task<SmtpSettings> ResolveSmtpSettingsAsync(CancellationToken ct)
    {
        // ── Tier 1: Company-specific SMTP ───────────────────────────────────────
        // Check if the current request is in a tenant context (has CompanyId).
        // If so, look up that company's SMTP settings in the master DB.
        var companyId = _tenantResolver.GetCurrentCompanyId();
        if (companyId.HasValue)
        {
            var companySettings = await _masterContext.CompanySystemSettings
                .AsNoTracking()
                .FirstOrDefaultAsync(s => s.CompanyId == companyId.Value, ct);

            // Only use company SMTP if SmtpHost is explicitly set (non-empty).
            // Null/empty SmtpHost means "use system defaults" (fall through to tier 2).
            if (companySettings != null && !string.IsNullOrWhiteSpace(companySettings.SmtpHost))
            {
                _logger.LogInformation(
                    "Using company SMTP ({Host}) for CompanyId {CompanyId}",
                    companySettings.SmtpHost, companyId.Value);

                return new SmtpSettings(
                    Host: companySettings.SmtpHost,
                    Port: companySettings.SmtpPort ?? 465,
                    Username: companySettings.SmtpUsername,
                    // Decrypt the SMTP password — it's stored encrypted at rest in the database.
                    Password: _credentialProtector.Decrypt(companySettings.SmtpPassword),
                    SenderEmail: companySettings.SmtpSenderEmail
                                 ?? companySettings.SmtpUsername ?? "",
                    SenderName: companySettings.SmtpSenderName ?? "Invoice",
                    UseSsl: companySettings.SmtpUseSsl ?? true);
            }
        }

        // ── Tier 2: System-wide SMTP from SystemConfiguration ────────────────────
        var dbConfig = await _systemConfigService.GetAsync(ct);
        if (!string.IsNullOrWhiteSpace(dbConfig.SmtpHost))
        {
            _logger.LogInformation("Using system SMTP ({Host})", dbConfig.SmtpHost);

            // Password is fetched separately via GetSmtpPasswordAsync — it's NOT in the DTO
            // (removed to prevent the UI from receiving and re-sending it on every save).
            var smtpPassword = await _systemConfigService.GetSmtpPasswordAsync(ct);

            return new SmtpSettings(
                Host: dbConfig.SmtpHost,
                Port: dbConfig.SmtpPort,
                Username: dbConfig.SmtpUsername,
                Password: smtpPassword,
                SenderEmail: dbConfig.SmtpSenderEmail,
                SenderName: dbConfig.SmtpSenderName,
                UseSsl: dbConfig.SmtpUseSsl);
        }

        // ── Tier 3: Fallback to appsettings.json ─────────────────────────────────
        var host = _configuration["SmtpSettings:Host"];
        if (!string.IsNullOrWhiteSpace(host))
        {
            _logger.LogInformation("Using appsettings.json SMTP ({Host})", host);

            return new SmtpSettings(
                Host: host,
                Port: int.Parse(_configuration["SmtpSettings:Port"] ?? "587"),
                Username: _configuration["SmtpSettings:Username"] ?? "",
                Password: _configuration["SmtpSettings:Password"] ?? "",
                SenderEmail: _configuration["SmtpSettings:SenderEmail"]
                             ?? _configuration["SmtpSettings:Username"] ?? "",
                SenderName: _configuration["SmtpSettings:SenderName"] ?? "Fakvio",
                UseSsl: bool.Parse(_configuration["SmtpSettings:UseSsl"] ?? "true"));
        }

        // No SMTP configured anywhere — fail fast with a clear error message
        throw new InvalidOperationException(
            "SMTP is not configured. Set it in Company SMTP Settings, System Settings, " +
            "or SmtpSettings:Host in appsettings.json.");
    }

    /// <summary>
    /// Tries to render subject and body from the default content template for the given type and language.
    /// Uses the language-aware overload of GetDefaultByTypeAsync which has a built-in fallback chain.
    /// Falls back to simple inline text if no template is found at all.
    /// </summary>
    private async Task<(string Subject, string HtmlBody)> RenderFromTemplateOrFallbackAsync(
        EContentTemplateType templateType,
        Dictionary<string, string> placeholders,
        string language,
        CancellationToken ct)
    {
        try
        {
            // Look up the default content template for this type and language (with fallback)
            var template = await _contentTemplateService.GetDefaultByTypeAsync(templateType, language, ct);
            if (template != null)
            {
                _logger.LogInformation("Using content template '{Name}' (ID {Id}) for type {Type}",
                    template.Name, template.Id, templateType);
                var (subject, htmlBody) = await _contentTemplateService.RenderTemplateAsync(template.Id, placeholders, ct);
                // Subject should never be null for email templates, but provide fallback just in case
                return (subject ?? "Email from Fakvio", htmlBody);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to render content template for type {Type}, falling back to default", templateType);
        }

        // Fallback — build a simple HTML email inline
        _logger.LogInformation("No content template found for type {Type}, using fallback HTML", templateType);
        return BuildFallbackEmail(templateType, placeholders, language);
    }

    /// <summary>
    /// Determines the correct MailKit SecureSocketOptions based on the SMTP port and UseSsl flag.
    ///
    /// Port 465 = implicit SSL (SslOnConnect) — entire session encrypted from the first byte.
    /// Port 587 = submission port — uses STARTTLS (connect plain, then upgrade to TLS).
    /// Port 25  = relay port — typically no encryption (internal servers).
    /// UseSsl=false = explicitly disabled encryption (for trusted internal relay servers).
    ///
    /// This replaces the previous bool-based ConnectAsync overload, which mapped true→SslOnConnect
    /// even on port 587, causing AuthenticationException when the server expected STARTTLS.
    /// </summary>
    private static SecureSocketOptions ResolveSocketOptions(int port, bool useSsl)
    {
        if (!useSsl)
            return SecureSocketOptions.None;

        // Port 465 is the only port that uses implicit SSL (SslOnConnect).
        // All other ports (587, 25, custom) should use StartTls when SSL is requested.
        return port == 465
            ? SecureSocketOptions.SslOnConnect
            : SecureSocketOptions.StartTls;
    }

    /// <summary>
    /// Builds a simple fallback email when no content template is configured for the given type.
    /// Czech is the default; English is used only when <paramref name="language"/> is "en" — the same rule as the
    /// seeded templates, so a client sees the same language whether or not a template row exists.
    /// </summary>
    internal static (string Subject, string HtmlBody) BuildFallbackEmail(
        EContentTemplateType templateType,
        Dictionary<string, string> placeholders,
        string language)
    {
        var get = (string key) => placeholders.TryGetValue(key, out var v) ? v : "";
        var czech = !string.Equals(language, "en", StringComparison.OrdinalIgnoreCase);

        return templateType switch
        {
            EContentTemplateType.InvoiceEmail => czech ? (
                Subject: $"Faktura {get("InvoiceNumber")} od {get("CompanyName")}",
                HtmlBody: $"""
                    <h2>Faktura {get("InvoiceNumber")}</h2>
                    <p>V příloze naleznete fakturu.</p>
                    <p><strong>Celkem k úhradě:</strong> {get("TotalWithVat")} {get("CurrencyCode")}</p>
                    <p><strong>Datum splatnosti:</strong> {get("DueDate")}</p>
                    <br/>
                    <p>Děkujeme za spolupráci.</p>
                    """
            ) : (
                Subject: $"Invoice {get("InvoiceNumber")} from {get("CompanyName")}",
                HtmlBody: $"""
                    <h2>Invoice {get("InvoiceNumber")}</h2>
                    <p>Please find the attached invoice.</p>
                    <p><strong>Total:</strong> {get("TotalWithVat")} {get("CurrencyCode")}</p>
                    <p><strong>Due date:</strong> {get("DueDate")}</p>
                    <br/>
                    <p>Thank you for your business.</p>
                    """
            ),
            EContentTemplateType.CreditNoteEmail => czech ? (
                Subject: $"Dobropis {get("InvoiceNumber")} od {get("CompanyName")}",
                HtmlBody: $"""
                    <h2>Dobropis {get("InvoiceNumber")}</h2>
                    <p>V příloze naleznete dobropis.</p>
                    <p><strong>Celkem:</strong> {get("TotalWithVat")} {get("CurrencyCode")}</p>
                    <br/>
                    <p>Děkujeme za spolupráci.</p>
                    """
            ) : (
                Subject: $"Credit note {get("InvoiceNumber")} from {get("CompanyName")}",
                HtmlBody: $"""
                    <h2>Credit Note {get("InvoiceNumber")}</h2>
                    <p>Please find the attached credit note.</p>
                    <p><strong>Total:</strong> {get("TotalWithVat")} {get("CurrencyCode")}</p>
                    <br/>
                    <p>Thank you for your business.</p>
                    """
            ),
            EContentTemplateType.AdvanceInvoiceEmail => czech ? (
                Subject: $"Zálohová faktura {get("InvoiceNumber")} od {get("CompanyName")}",
                HtmlBody: $"""
                    <h2>Zálohová faktura {get("InvoiceNumber")}</h2>
                    <p>V příloze naleznete zálohovou fakturu.</p>
                    <p><strong>Částka k úhradě:</strong> {get("TotalWithVat")} {get("CurrencyCode")}</p>
                    <p><strong>Datum splatnosti:</strong> {get("DueDate")}</p>
                    <br/>
                    <p>Upozorňujeme, že zálohová faktura není daňovým dokladem. Daňový doklad Vám bude vystaven po přijetí platby.</p>
                    <p>Děkujeme.</p>
                    """
            ) : (
                Subject: $"Advance invoice {get("InvoiceNumber")} from {get("CompanyName")}",
                HtmlBody: $"""
                    <h2>Advance Invoice {get("InvoiceNumber")}</h2>
                    <p>Please find the attached advance invoice (pro-forma).</p>
                    <p><strong>Amount due:</strong> {get("TotalWithVat")} {get("CurrencyCode")}</p>
                    <p><strong>Due date:</strong> {get("DueDate")}</p>
                    <br/>
                    <p>Please note: this is not a tax document. A tax receipt will be issued upon receipt of payment.</p>
                    <p>Thank you for your business.</p>
                    """
            ),
            EContentTemplateType.TaxReceiptForAdvanceEmail => czech ? (
                Subject: $"Daňový doklad o přijaté platbě {get("InvoiceNumber")} od {get("CompanyName")}",
                HtmlBody: $"""
                    <h2>Daňový doklad o přijaté platbě {get("InvoiceNumber")}</h2>
                    <p>V příloze naleznete daňový doklad o přijaté záloze.</p>
                    <p><strong>Uhrazená částka:</strong> {get("TotalWithVat")} {get("CurrencyCode")}</p>
                    <br/>
                    <p>Děkujeme za platbu.</p>
                    """
            ) : (
                Subject: $"Tax receipt for advance {get("InvoiceNumber")} from {get("CompanyName")}",
                HtmlBody: $"""
                    <h2>Tax Receipt for Advance Payment {get("InvoiceNumber")}</h2>
                    <p>Please find the attached tax receipt for the advance payment we have received.</p>
                    <p><strong>Total:</strong> {get("TotalWithVat")} {get("CurrencyCode")}</p>
                    <br/>
                    <p>Thank you for your payment.</p>
                    """
            ),
            EContentTemplateType.InvitationEmail => czech ? (
                Subject: $"Pozvánka do {get("AppName")} – nastavte si heslo",
                HtmlBody: $"""
                    <div style="font-family: Arial, sans-serif; max-width: 600px; margin: 0 auto;">
                    <h2>Vítejte v {get("AppName")}</h2>
                    <p>Dobrý den, <strong>{get("FullName")}</strong>,</p>
                    <p>Heslo si nastavíte kliknutím na odkaz níže:</p>
                    <p><a href="{get("InvitationLink")}">Nastavit heslo</a></p>
                    <p>Odkaz je platný 48 hodin.</p>
                    </div>
                    """
            ) : (
                Subject: $"Invitation to {get("AppName")} — Set your password",
                HtmlBody: $"""
                    <div style="font-family: Arial, sans-serif; max-width: 600px; margin: 0 auto;">
                    <h2>Welcome to {get("AppName")}</h2>
                    <p>Hello <strong>{get("FullName")}</strong>,</p>
                    <p>Please set your password by clicking the link below:</p>
                    <p><a href="{get("InvitationLink")}">Set Password</a></p>
                    <p>This link is valid for 48 hours.</p>
                    </div>
                    """
            ),
            EContentTemplateType.ReminderEmail => czech ? (
                Subject: $"Upomínka – faktura {get("InvoiceNumber")} po splatnosti",
                HtmlBody: $"""
                    <h2>Upomínka k úhradě</h2>
                    <p>Faktura <strong>{get("InvoiceNumber")}</strong> je po splatnosti.</p>
                    <p><strong>Celkem k úhradě:</strong> {get("TotalWithVat")} {get("CurrencyCode")}</p>
                    <p><strong>Datum splatnosti:</strong> {get("DueDate")}</p>
                    <p>Prosíme o úhradu v nejbližším možném termínu.</p>
                    """
            ) : (
                Subject: $"Payment reminder — Invoice {get("InvoiceNumber")}",
                HtmlBody: $"""
                    <h2>Payment Reminder</h2>
                    <p>Invoice <strong>{get("InvoiceNumber")}</strong> is overdue.</p>
                    <p><strong>Total:</strong> {get("TotalWithVat")} {get("CurrencyCode")}</p>
                    <p><strong>Due date:</strong> {get("DueDate")}</p>
                    <p>Please arrange payment at your earliest convenience.</p>
                    """
            ),
            _ => (
                Subject: czech ? "E-mail od Fakvio" : "Email from Fakvio",
                HtmlBody: czech ? "<p>Obsah e-mailu</p>" : "<p>Email content</p>"
            )
        };
    }

    /// <summary>
    /// Resolves the application name from SystemConfiguration DB, with fallback to appsettings.json.
    /// Used in email placeholders ({{AppName}}).
    /// </summary>
    private async Task<string> ResolveAppNameAsync(CancellationToken ct)
    {
        var dbConfig = await _systemConfigService.GetAsync(ct);
        if (!string.IsNullOrWhiteSpace(dbConfig.AppName))
            return dbConfig.AppName;

        return _configuration["AppSettings:Name"] ?? "Fakvio";
    }
}

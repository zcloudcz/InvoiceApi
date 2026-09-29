using Fakvio.Application.Exceptions;
using Fakvio.Application.Service;
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
    private readonly IIsdocExportService _isdocExportService;
    private readonly IUblExportService _ublExportService;
    private readonly IContentTemplateService _contentTemplateService;
    private readonly ISystemConfigurationService _systemConfigService;
    private readonly ICredentialProtector _credentialProtector;
    private readonly ITenantResolver _tenantResolver;
    private readonly IConfiguration _configuration;
    private readonly ILogger<EmailService> _logger;

    /// <summary>
    /// Internal record to hold resolved SMTP configuration from any tier.
    /// This avoids passing 7+ parameters around — a single object holds everything needed
    /// to connect to an SMTP server and send an email.
    /// </summary>
    private record SmtpSettings(
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
        IIsdocExportService isdocExportService,
        IUblExportService ublExportService,
        IContentTemplateService contentTemplateService,
        ISystemConfigurationService systemConfigService,
        ICredentialProtector credentialProtector,
        ITenantResolver tenantResolver,
        IConfiguration configuration,
        ILogger<EmailService> logger)
    {
        _context = context;
        _masterContext = masterContext;
        _pdfExportService = pdfExportService;
        _isdocExportService = isdocExportService;
        _ublExportService = ublExportService;
        _contentTemplateService = contentTemplateService;
        _systemConfigService = systemConfigService;
        _credentialProtector = credentialProtector;
        _tenantResolver = tenantResolver;
        _configuration = configuration;
        _logger = logger;
    }

    /// <inheritdoc />
    public async Task SendInvoiceEmailAsync(long invoiceId, string recipientEmail, CancellationToken ct = default)
    {
        _logger.LogInformation("Sending invoice {InvoiceId} via email to {Email}", invoiceId, recipientEmail);

        // Load the invoice with related data for placeholder substitution.
        // Include Client to read the client's preferred language for template resolution.
        // Client.Address is included on top of the ISDOC-era set of includes — F1.9 needs the
        // buyer's country to decide whether a UBL attachment applies (UblCodes.CountryToIso2).
        var invoice = await _context.Invoice
            .Include(i => i.Currency)
            .Include(i => i.Issuer)
            .Include(i => i.Client)
                .ThenInclude(c => c!.Address)
            .FirstOrDefaultAsync(i => i.Id == invoiceId, ct)
            ?? throw new KeyNotFoundException($"Invoice with ID {invoiceId} not found.");

        // Build a safe file-name prefix from the document type.
        // Each type gets its own recognisable prefix so the attachment name in the inbox
        // immediately tells the recipient what they received.
        var prefix = invoice.DocumentType switch
        {
            EDocumentType.CreditNote => "CreditNote",
            EDocumentType.Proforma => "Proforma",
            EDocumentType.TaxReceiptForAdvance => "TaxReceipt",
            _ => "Invoice"
        };
        var docNumber = invoice.DocumentNumber ?? invoiceId.ToString();

        // Generate the PDF attachment using the PDF export service.
        var pdfBytes = await _pdfExportService.GenerateInvoicePdfAsync(invoiceId, ct);
        var pdfFileName = $"{prefix}_{docNumber}.pdf";

        // Generate the ISDOC XML attachment.
        // ISDOC 6.0.2 is the Czech electronic invoice standard read by Pohoda, Money, Helios.
        // The attachment is always included — per-client opt-out is a separate future feature.
        var isdocBytes = await _isdocExportService.ExportInvoiceAsync(invoiceId, ct);
        var isdocFileName = $"{prefix}_{docNumber}.isdoc";

        // UBL/Peppol attachment (ADR 0002, F1.9) — only for a buyer who is actually reachable
        // via Peppol: a Slovak buyer (SK e-invoicing 2027) or any buyer with an explicit
        // Client.PeppolId override (F1.8). A pre-flight failure (missing Peppol ID, incomplete
        // address, …) just skips the attachment — the email itself must never fail because the
        // UBL side of the document is not ready yet; PDF + ISDOC still go out as before.
        // Sending e-invoice XML by email instead of through a Peppol courier is only lawful with
        // the recipient's consent ([FAQ] I/66) — USERGUIDE §2.8a tells the user that.
        byte[]? ublBytes = null;
        string? ublFileName = null;
        var buyerCountry = UblCodes.CountryToIso2(
            invoice.Client?.Address?.FirstOrDefault(a => a.IsPrimary)?.Country
                ?? invoice.Client?.Address?.FirstOrDefault()?.Country);
        var buyerIsPeppolReachable = buyerCountry == "SK" || !string.IsNullOrWhiteSpace(invoice.Client?.PeppolId);
        if (buyerIsPeppolReachable)
        {
            try
            {
                ublBytes = await _ublExportService.ExportInvoiceAsync(invoiceId, ct);
                ublFileName = $"{prefix}_{docNumber}.xml";
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw; // A caller-requested cancellation must still abort the whole send.
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

        _logger.LogInformation(
            "Invoice {InvoiceId} attachments prepared: PDF={PdfFile} ({PdfBytes} B), ISDOC={IsdocFile} ({IsdocBytes} B){UblInfo}",
            invoiceId, pdfFileName, pdfBytes.Length, isdocFileName, isdocBytes.Length,
            ublBytes != null ? $", UBL={ublFileName} ({ublBytes.Length} B)" : "");

        // Build placeholders dictionary for template substitution
        var placeholders = new Dictionary<string, string>
        {
            ["InvoiceNumber"] = docNumber,
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

        // Try to render from the default content template for the client's language, fall back to simple HTML
        var (subject, htmlBody) = await RenderFromTemplateOrFallbackAsync(templateType, placeholders, clientLanguage, ct);

        // Build and send the email with PDF + ISDOC (+ UBL when applicable, F1.9).
        // We call the internal builder directly instead of SendEmailAsync because
        // the public SendEmailAsync signature supports only one optional attachment
        // and we don't want to break that public interface (per issue #16 analysis).
        await SendInvoiceMessageAsync(
            recipientEmail, subject, htmlBody,
            pdfBytes, pdfFileName,
            isdocBytes, isdocFileName,
            ublBytes, ublFileName,
            ct);

        // Mark the invoice as sent by email in the database
        invoice.IsSentByEmail = true;
        invoice.LastSentByEmailAt = DateTime.UtcNow;
        await _context.SaveChangesAsync(ct);

        var attachmentCount = ublBytes != null ? 3 : 2;
        var attachmentNames = ublBytes != null
            ? $"{pdfFileName}, {isdocFileName}, {ublFileName}"
            : $"{pdfFileName}, {isdocFileName}";
        _logger.LogInformation(
            "Invoice {InvoiceId} email sent successfully to {Email} with {AttachmentCount} attachments: {Attachments}",
            invoiceId, recipientEmail, attachmentCount, attachmentNames);
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
    internal async Task SendInvoiceMessageAsync(
        string to, string subject, string htmlBody,
        byte[] pdfBytes, string pdfFileName,
        byte[] isdocBytes, string isdocFileName,
        byte[]? ublBytes = null, string? ublFileName = null,
        CancellationToken ct = default)
    {
        // Resolve SMTP settings using the 3-tier priority chain
        var smtp = await ResolveSmtpSettingsAsync(ct);

        // Build the MimeMessage with HTML body + attachments
        var message = BuildInvoiceMessage(
            smtp.SenderName, smtp.SenderEmail,
            to, subject, htmlBody,
            pdfBytes, pdfFileName,
            isdocBytes, isdocFileName,
            ublBytes, ublFileName);

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
    internal static MimeMessage BuildInvoiceMessage(
        string senderName, string senderEmail,
        string to, string subject, string htmlBody,
        byte[] pdfBytes, string pdfFileName,
        byte[] isdocBytes, string isdocFileName,
        byte[]? ublBytes = null, string? ublFileName = null)
    {
        var message = new MimeMessage();
        message.From.Add(new MailboxAddress(senderName, senderEmail));
        message.To.Add(MailboxAddress.Parse(to));
        message.Subject = subject;

        var bodyBuilder = new BodyBuilder { HtmlBody = htmlBody };

        // PDF invoice — standard document type
        bodyBuilder.Attachments.Add(pdfFileName, pdfBytes, ContentType.Parse("application/pdf"));

        // ISDOC XML — Czech electronic invoice standard (ISO/IEC 19845 based).
        // Content type "application/xml" is the MIME type for XML documents (RFC 7303).
        bodyBuilder.Attachments.Add(isdocFileName, isdocBytes, ContentType.Parse("application/xml"));

        // UBL/Peppol XML (F1.9) — only when the caller resolved one (SK buyer or explicit
        // Client.PeppolId override, and the pre-flight actually passed).
        if (ublBytes != null && ublFileName != null)
            bodyBuilder.Attachments.Add(ublFileName, ublBytes, ContentType.Parse("application/xml"));

        message.Body = bodyBuilder.ToMessageBody();
        return message;
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
    private async Task SendViaSMTPAsync(SmtpSettings smtp, MimeMessage message, CancellationToken ct)
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
        return BuildFallbackEmail(templateType, placeholders);
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
    /// </summary>
    private static (string Subject, string HtmlBody) BuildFallbackEmail(
        EContentTemplateType templateType,
        Dictionary<string, string> placeholders)
    {
        var get = (string key) => placeholders.TryGetValue(key, out var v) ? v : "";

        return templateType switch
        {
            EContentTemplateType.InvoiceEmail or EContentTemplateType.CreditNoteEmail => (
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
            EContentTemplateType.AdvanceInvoiceEmail => (
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
            EContentTemplateType.TaxReceiptForAdvanceEmail => (
                Subject: $"Tax receipt for advance {get("InvoiceNumber")} from {get("CompanyName")}",
                HtmlBody: $"""
                    <h2>Tax Receipt for Advance Payment {get("InvoiceNumber")}</h2>
                    <p>Please find the attached tax receipt for the advance payment we have received.</p>
                    <p><strong>Total:</strong> {get("TotalWithVat")} {get("CurrencyCode")}</p>
                    <br/>
                    <p>Thank you for your payment.</p>
                    """
            ),
            EContentTemplateType.InvitationEmail => (
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
            EContentTemplateType.ReminderEmail => (
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
                Subject: "Email from Fakvio",
                HtmlBody: "<p>Email content</p>"
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

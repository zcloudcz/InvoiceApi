using System.Security.Cryptography;
using System.Text;
using Fakvio.Application.QrPayment;
using Fakvio.Application.Service;
using Fakvio.Contracts.Dto.Import;
using Fakvio.Contracts.Dto.ReceivedInvoice;
using Fakvio.Domain.Entities;
using Fakvio.Domain.Enums;
using Fakvio.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Fakvio.Infrastructure.Service;

/// <summary>
/// Per-tenant processor for inbound invoice emails.
///
/// Pipeline:
///   1. Guard (active mailbox, dedup)
///   2. Archive (persist InboundInvoiceEmail)
///   3. Extract (ISDOC > PDF/QR > PDF/AI > email body AI)
///   4. Classify (issuer IČO vs company IČO → direction)
///   5. Create (ReceivedInvoice or Invoice)
///   6. Attach (save PDF/ISDOC as FileAttachment)
///   7. Notify (all tenant users)
/// </summary>
public class InvoiceEmailProcessor : IInvoiceEmailProcessor
{
    private readonly TenantDbContext _context;
    private readonly IIsdocImportParser _isdocParser;
    private readonly IInvoiceEmailClassifier _classifier;
    private readonly IInvoiceImportService _importService;
    private readonly IClientService _clientService;
    private readonly IReceivedInvoiceService _receivedInvoiceService;
    private readonly INotificationService _notificationService;
    private readonly IFileAttachmentService? _fileAttachmentService;
    private readonly ILogger<InvoiceEmailProcessor> _logger;

    private const decimal ConfidenceThreshold = 0.7m;
    private const int MaxBodyLength = 1_048_576; // 1 MB

    public InvoiceEmailProcessor(
        TenantDbContext context,
        IIsdocImportParser isdocParser,
        IInvoiceEmailClassifier classifier,
        IInvoiceImportService importService,
        IClientService clientService,
        IReceivedInvoiceService receivedInvoiceService,
        INotificationService notificationService,
        ILogger<InvoiceEmailProcessor> logger,
        IFileAttachmentService? fileAttachmentService = null)
    {
        _context = context;
        _isdocParser = isdocParser;
        _classifier = classifier;
        _importService = importService;
        _clientService = clientService;
        _receivedInvoiceService = receivedInvoiceService;
        _notificationService = notificationService;
        _fileAttachmentService = fileAttachmentService;
        _logger = logger;
    }

    /// <inheritdoc />
    public async Task<EInvoiceEmailStatus> ProcessAsync(
        InvoiceEmailPayload payload,
        IReadOnlyList<EmailAttachment> attachments,
        long companyId,
        CancellationToken ct = default)
    {
        // 1. Guard — mailbox active?
        var mailbox = await _context.InvoiceMailbox
            .FirstOrDefaultAsync(m => m.Id == payload.InvoiceMailboxId, ct);

        if (mailbox == null || !mailbox.IsActive)
        {
            _logger.LogDebug("Invoice mailbox {Id} not found or inactive — skipping", payload.InvoiceMailboxId);
            return EInvoiceEmailStatus.Ignored;
        }

        if (payload.ServerReceivedAt < mailbox.ActiveFrom)
        {
            _logger.LogDebug("Email older than ActiveFrom ({ActiveFrom}) — skipping", mailbox.ActiveFrom);
            return EInvoiceEmailStatus.Ignored;
        }

        // 2. Dedup
        var hash = ComputeDeduplicationHash(
            payload.InvoiceMailboxId, payload.MessageId, payload.ImapUid, payload.ServerReceivedAt);

        var exists = await _context.InboundInvoiceEmail
            .AnyAsync(e => e.InvoiceMailboxId == payload.InvoiceMailboxId
                        && e.DeduplicationHash == hash, ct);
        if (exists)
        {
            _logger.LogDebug("Duplicate email detected (hash={Hash}) — skipping", hash);
            return EInvoiceEmailStatus.Ignored;
        }

        // 3. Archive
        var email = new InboundInvoiceEmail
        {
            InvoiceMailboxId = payload.InvoiceMailboxId,
            MessageId = payload.MessageId,
            ImapUid = payload.ImapUid,
            ServerReceivedAt = payload.ServerReceivedAt,
            FromAddress = payload.FromAddress,
            FromDisplayName = payload.FromDisplayName,
            ToAddress = payload.ToAddress,
            Subject = payload.Subject,
            EmailDate = payload.EmailDate,
            TextBody = Truncate(payload.TextBody, MaxBodyLength),
            HtmlBody = Truncate(payload.HtmlBody, MaxBodyLength),
            BodyTruncated = (payload.TextBody?.Length ?? 0) > MaxBodyLength
                         || (payload.HtmlBody?.Length ?? 0) > MaxBodyLength,
            DeduplicationHash = hash,
            Status = EInvoiceEmailStatus.Pending,
            AttachmentCount = attachments.Count,
            HasPdf = attachments.Any(a => IsPdf(a)),
            HasIsdoc = attachments.Any(a => IsIsdoc(a)),
        };

        _context.InboundInvoiceEmail.Add(email);
        mailbox.EmailsReceivedCount++;
        mailbox.LastEmailReceivedAt = DateTime.UtcNow;
        await _context.SaveChangesAsync(ct);

        _logger.LogInformation(
            "Invoice email archived: Id={EmailId} From={From} Subject={Subject} Attachments={Count}",
            email.Id, payload.FromAddress, payload.Subject, attachments.Count);

        // 4. Extract invoice data
        try
        {
            email.ProcessAttempts++;
            var (extractedData, direction) = await ExtractAndClassifyAsync(
                email, payload, attachments, companyId, ct);

            if (extractedData == null)
            {
                email.Status = EInvoiceEmailStatus.Failed;
                email.StatusError = "No invoice data could be extracted from email or attachments.";
                await _context.SaveChangesAsync(ct);

                await NotifyAsync(ENotificationType.InvoiceEmailNeedsReview,
                    "Email bez faktury",
                    $"Email od {payload.FromAddress} neobsahuje rozpoznatelnou fakturu.",
                    email.Id, ct);

                return EInvoiceEmailStatus.Failed;
            }

            email.Direction = direction;

            // 5. Determine confidence threshold
            var isLowConfidence = email.ClassificationConfidence.HasValue
                                 && email.ClassificationConfidence.Value < ConfidenceThreshold;

            // 6. Create invoice
            long? createdId = null;
            string entityType;

            if (direction == EInvoiceDirection.Received)
            {
                createdId = await CreateReceivedInvoiceAsync(extractedData, ct);
                email.ReceivedInvoiceId = createdId;
                entityType = "ReceivedInvoice";
            }
            else
            {
                createdId = await CreateIssuedInvoiceAsync(extractedData, ct);
                email.InvoiceId = createdId;
                entityType = "Invoice";
            }

            email.Status = isLowConfidence ? EInvoiceEmailStatus.NeedsReview : EInvoiceEmailStatus.Imported;
            await _context.SaveChangesAsync(ct);

            // 7. Attach original files
            if (createdId.HasValue && _fileAttachmentService != null)
            {
                await AttachFilesAsync(attachments, entityType, createdId.Value, ct);
            }

            // 8. Notify
            var directionLabel = direction == EInvoiceDirection.Received ? "přijatá" : "vydaná";
            var docNumber = extractedData.DocumentNumber ?? "?";
            var notifType = isLowConfidence
                ? ENotificationType.InvoiceEmailNeedsReview
                : ENotificationType.InvoiceEmailImported;

            await NotifyAsync(notifType,
                $"Faktura {directionLabel} importována",
                $"Faktura {docNumber} od {payload.FromAddress} automaticky importována jako {directionLabel}.",
                createdId ?? email.Id,
                ct,
                createdId.HasValue ? entityType : "InboundInvoiceEmail");

            _logger.LogInformation(
                "Invoice email processed: EmailId={EmailId} Direction={Direction} DocNum={DocNum} Status={Status}",
                email.Id, direction, docNumber, email.Status);

            return email.Status;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to process invoice email {EmailId}", email.Id);
            email.Status = EInvoiceEmailStatus.Failed;
            email.StatusError = ex.Message.Length > 2000 ? ex.Message[..2000] : ex.Message;
            await _context.SaveChangesAsync(ct);

            await NotifyAsync(ENotificationType.InvoiceEmailNeedsReview,
                "Import faktury selhal",
                $"Zpracování emailu od {payload.FromAddress} selhalo: {email.StatusError}",
                email.Id, ct, "InboundInvoiceEmail");

            return EInvoiceEmailStatus.Failed;
        }
    }

    // ─── Extraction + Classification ─────────────────────────────────────

    private async Task<(InvoiceExtractedData? data, EInvoiceDirection direction)> ExtractAndClassifyAsync(
        InboundInvoiceEmail email,
        InvoiceEmailPayload payload,
        IReadOnlyList<EmailAttachment> attachments,
        long companyId,
        CancellationToken ct)
    {
        InvoiceExtractedData? data = null;

        // Priority 1: ISDOC XML attachment
        var isdocAttachment = attachments.FirstOrDefault(a => IsIsdoc(a));
        if (isdocAttachment != null)
        {
            var xml = Encoding.UTF8.GetString(isdocAttachment.Content);
            data = _isdocParser.Parse(xml);
            if (data != null)
                _logger.LogDebug("ISDOC extraction succeeded for email {EmailId}", email.Id);
        }

        // Priority 2: PDF attachment via existing import pipeline
        if (data == null)
        {
            var pdfAttachment = attachments.FirstOrDefault(a => IsPdf(a));
            if (pdfAttachment != null)
            {
                try
                {
                    var preview = await _importService.PreviewImportAsync(
                        pdfAttachment.Content,
                        pdfAttachment.FileName,
                        EImportTarget.ReceivedInvoice,
                        ct);

                    if (preview != null)
                    {
                        data = MapPreviewToExtractedData(preview);
                        _logger.LogDebug("PDF extraction succeeded for email {EmailId}", email.Id);
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "PDF extraction failed for email {EmailId}", email.Id);
                }
            }
        }

        if (data == null)
        {
            _logger.LogDebug("No invoice data extracted from attachments for email {EmailId}", email.Id);
            return (null, EInvoiceDirection.Received);
        }

        // Classify direction — programmatic first (IČO comparison), AI fallback
        var issuer = await _context.Client
            .AsNoTracking()
            .FirstOrDefaultAsync(c => c.IsIssuer, ct);

        var companyIco = issuer?.RegistrationNumber ?? "";
        var companyName = issuer?.CompanyName ?? "";

        EInvoiceDirection direction;

        if (!string.IsNullOrEmpty(data.IssuerRegistrationNumber) && !string.IsNullOrEmpty(companyIco))
        {
            // Deterministic: compare IČO directly
            direction = data.IssuerRegistrationNumber.Trim() == companyIco.Trim()
                ? EInvoiceDirection.Issued
                : EInvoiceDirection.Received;
            email.ClassificationConfidence = 1.0m;

            _logger.LogDebug(
                "Direction classified by IČO comparison: Issuer={IssuerIco} Company={CompanyIco} → {Direction}",
                data.IssuerRegistrationNumber, companyIco, direction);
        }
        else
        {
            // AI fallback
            var result = await _classifier.ClassifyAsync(
                payload.TextBody, null, null, companyIco, companyName, companyId, ct);

            direction = result.Direction;
            email.ClassificationConfidence = result.Confidence;

            // Enrich extracted data with AI-provided IČO if available
            if (!string.IsNullOrEmpty(result.IssuerRegistrationNumber))
                data.IssuerRegistrationNumber ??= result.IssuerRegistrationNumber;
            if (!string.IsNullOrEmpty(result.IssuerName))
                data.IssuerName ??= result.IssuerName;

            _logger.LogDebug(
                "Direction classified by AI: {Direction} (confidence={Confidence})",
                direction, result.Confidence);
        }

        return (data, direction);
    }

    // ─── Invoice creation ────────────────────────────────────────────────

    private async Task<long?> CreateReceivedInvoiceAsync(InvoiceExtractedData data, CancellationToken ct)
    {
        // Find or create supplier client
        long supplierId;
        if (!string.IsNullOrEmpty(data.IssuerRegistrationNumber))
        {
            var existing = await _context.Client
                .AsNoTracking()
                .FirstOrDefaultAsync(c => c.RegistrationNumber == data.IssuerRegistrationNumber
                                       && !c.IsIssuer, ct);

            if (existing != null)
            {
                supplierId = existing.Id;
            }
            else
            {
                var created = await _clientService.CreateClientAsync(new Contracts.Dto.Client.CreateClientDto
                {
                    RegistrationNumber = data.IssuerRegistrationNumber,
                    CompanyName = data.IssuerName ?? $"Imported — {data.IssuerRegistrationNumber}",
                }, ct);
                supplierId = created.Id;
            }
        }
        else
        {
            _logger.LogWarning("No issuer IČO found — cannot create received invoice without supplier");
            return null;
        }

        // Resolve currency ID from code
        var currencyCode = data.Currency ?? "CZK";
        var currency = await _context.Currency
            .AsNoTracking()
            .FirstOrDefaultAsync(c => c.Code == currencyCode, ct);

        var items = data.Items?.Select(i => new CreateReceivedInvoiceItemDto
        {
            Description = i.Description ?? "Položka",
            Quantity = i.Quantity ?? 1m,
            UnitPrice = i.UnitPrice ?? 0m,
            VatRatePercentage = i.VatRate ?? 0m,
            Unit = i.Unit ?? "ks",
        }).ToList();

        // If no items extracted, create a single summary item from totals
        if (items == null || items.Count == 0)
        {
            items =
            [
                new CreateReceivedInvoiceItemDto
                {
                    Description = data.DocumentNumber != null
                        ? $"Faktura {data.DocumentNumber}"
                        : "Položka faktury",
                    Quantity = 1m,
                    UnitPrice = data.TotalBeforeVat ?? data.TotalAmount ?? 0m,
                    VatRatePercentage = 21m,
                    Unit = "ks",
                }
            ];
        }

        var dto = new CreateReceivedInvoiceDto
        {
            DocumentNumber = data.DocumentNumber,
            SupplierId = supplierId,
            IssueDate = data.IssueDate,
            DueDate = data.DueDate,
            TaxableSupplyDate = data.TaxableSupplyDate,
            ReceivedDate = DateTime.UtcNow,
            VariableSymbol = data.VariableSymbol,
            BankAccountNumber = data.BankAccountNumber,
            IBAN = data.IBAN,
            SWIFT = data.SWIFT,
            CurrencyId = currency?.Id ?? 1,
            Items = items,
        };

        var result = await _receivedInvoiceService.CreateAsync(dto, ct);
        return result?.Id;
    }

    private async Task<long?> CreateIssuedInvoiceAsync(InvoiceExtractedData data, CancellationToken ct)
    {
        // For issued invoices received by email (confirmation copies), we just log for now.
        // Full implementation would call IInvoiceService.CreateInvoiceAsync
        // but issued invoices typically already exist in the system.
        _logger.LogInformation(
            "Issued invoice detected in email: DocNum={DocNum} — skipping auto-create (confirmation copy)",
            data.DocumentNumber);
        return null;
    }

    // ─── File attachment ─────────────────────────────────────────────────

    private async Task AttachFilesAsync(
        IReadOnlyList<EmailAttachment> attachments,
        string entityType,
        long entityId,
        CancellationToken ct)
    {
        foreach (var att in attachments.Where(a => IsPdf(a) || IsIsdoc(a)))
        {
            try
            {
                if (_fileAttachmentService != null)
                {
                    await _fileAttachmentService.UploadAsync(
                        new Contracts.Dto.FileAttachment.FileAttachmentUploadDto
                        {
                            EntityName = entityType,
                            RecordId = entityId,
                            FileName = att.FileName,
                            ContentType = att.ContentType,
                            FileContent = att.Content,
                            Description = "Imported from email",
                        }, ct);
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to attach file {FileName} to {EntityType}/{EntityId}",
                    att.FileName, entityType, entityId);
            }
        }
    }

    // ─── Notifications ───────────────────────────────────────────────────

    private async Task NotifyAsync(
        ENotificationType type, string title, string message,
        long entityId, CancellationToken ct, string entityType = "InboundInvoiceEmail")
    {
        try
        {
            await _notificationService.CreateForAllUsersAsync(type, title, message, entityId, entityType, ct);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to send notification for invoice email");
        }
    }

    // ─── Helpers ─────────────────────────────────────────────────────────

    private static string ComputeDeduplicationHash(
        long mailboxId, string messageId, string? imapUid, DateTime receivedAt)
    {
        var input = $"{mailboxId}|{messageId}|{imapUid}|{receivedAt:O}";
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(input));
        return Convert.ToHexStringLower(bytes);
    }

    private static string? Truncate(string? value, int maxLength)
        => value != null && value.Length > maxLength ? value[..maxLength] : value;

    private static bool IsPdf(EmailAttachment att)
        => att.ContentType.Contains("pdf", StringComparison.OrdinalIgnoreCase)
        || att.FileName.EndsWith(".pdf", StringComparison.OrdinalIgnoreCase);

    private static bool IsIsdoc(EmailAttachment att)
        => att.FileName.EndsWith(".isdoc", StringComparison.OrdinalIgnoreCase)
        || att.FileName.EndsWith(".isdocx", StringComparison.OrdinalIgnoreCase)
        || att.ContentType.Contains("isdoc", StringComparison.OrdinalIgnoreCase);

    private static InvoiceExtractedData MapPreviewToExtractedData(
        Contracts.Dto.Import.InvoiceImportPreviewDto p) => new()
    {
        DocumentNumber = p.DocumentNumber,
        IssueDate = p.IssueDate,
        DueDate = p.DueDate,
        TaxableSupplyDate = p.TaxableSupplyDate,
        TotalAmount = p.TotalAmount,
        TotalVat = p.TotalVat,
        TotalBeforeVat = p.TotalBeforeVat,
        Currency = p.Currency,
        VariableSymbol = p.VariableSymbol,
        IBAN = p.IBAN,
        SWIFT = p.SWIFT,
        BankAccountNumber = p.BankAccountNumber,
        IssuerName = p.IssuerName,
        IssuerRegistrationNumber = p.IssuerRegistrationNumber,
        IssuerTaxNumber = p.IssuerTaxNumber,
        RecipientName = p.RecipientName,
        RecipientRegistrationNumber = p.RecipientRegistrationNumber,
        RecipientTaxNumber = p.RecipientTaxNumber,
        Source = Enum.TryParse<EExtractionSource>(p.ExtractionSource, out var src)
            ? src : EExtractionSource.Merged,
    };
}

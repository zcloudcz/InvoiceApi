using System.Security.Cryptography;
using System.Text;
using Fakvio.Application.QrPayment;
using Fakvio.Application.Service;
using Fakvio.Contracts.Dto.Import;
using Fakvio.Contracts.Dto.Invoice;
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
///   3. Extract (ISDOC > UBL > PDF/QR > PDF/AI > email body AI)
///   4. Classify (issuer IČO vs company IČO → direction)
///   5. Create (ReceivedInvoice or Invoice)
///   6. Attach (save PDF/ISDOC/UBL as FileAttachment)
///   7. Notify (all tenant users)
/// </summary>
public class InvoiceEmailProcessor : IInvoiceEmailProcessor
{
    private readonly TenantDbContext _context;
    private readonly IIsdocImportParser _isdocParser;
    private readonly IUblImportParser _ublParser;
    private readonly IInvoiceEmailClassifier _classifier;
    private readonly IInvoiceImportService _importService;
    private readonly IClientService _clientService;
    private readonly IReceivedInvoiceService _receivedInvoiceService;
    private readonly IInvoiceService _invoiceService;
    private readonly INotificationService _notificationService;
    private readonly IFileAttachmentService? _fileAttachmentService;
    private readonly ILogger<InvoiceEmailProcessor> _logger;

    private const decimal ConfidenceThreshold = 0.7m;
    private const int MaxBodyLength = 1_048_576; // 1 MB

    public InvoiceEmailProcessor(
        TenantDbContext context,
        IIsdocImportParser isdocParser,
        IUblImportParser ublParser,
        IInvoiceEmailClassifier classifier,
        IInvoiceImportService importService,
        IClientService clientService,
        IReceivedInvoiceService receivedInvoiceService,
        IInvoiceService invoiceService,
        INotificationService notificationService,
        ILogger<InvoiceEmailProcessor> logger,
        IFileAttachmentService? fileAttachmentService = null)
    {
        _context = context;
        _isdocParser = isdocParser;
        _ublParser = ublParser;
        _classifier = classifier;
        _importService = importService;
        _clientService = clientService;
        _receivedInvoiceService = receivedInvoiceService;
        _invoiceService = invoiceService;
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
            payload.InvoiceMailboxId, payload.MessageId, payload.ImapUid);

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
            // Note: no HasUbl flag — InboundInvoiceEmail's Has* columns are a fixed schema
            // and F1.10 intentionally makes zero DB changes (see ADR §4.1.4: the only
            // planned schema change in phase 1 is Client.PeppolId, tracked in task F1.8).
            // UBL attachments are still detected and processed below via IsUbl(); if a
            // "has UBL" filter becomes useful in the UI, add the column then.
            HasIsdoc = attachments.Any(a => IsIsdoc(a)),
        };

        _context.InboundInvoiceEmail.Add(email);
        mailbox.EmailsReceivedCount++;
        mailbox.LastEmailReceivedAt = DateTime.UtcNow;
        await _context.SaveChangesAsync(ct);

        _logger.LogInformation(
            "Invoice email archived: Id={EmailId} From={From} Subject={Subject} Attachments={Count}",
            email.Id, payload.FromAddress, payload.Subject, attachments.Count);

        // 4. Process each attachment independently (batch: one email can contain multiple invoices)
        try
        {
            email.ProcessAttempts++;
            var invoiceAttachments = attachments.Where(a => IsPdf(a) || IsIsdoc(a) || IsUbl(a)).ToList();

            if (invoiceAttachments.Count == 0)
            {
                email.Status = EInvoiceEmailStatus.Failed;
                email.StatusError = "No invoice data could be extracted from email or attachments.";
                await _context.SaveChangesAsync(ct);

                await NotifyAsync(ENotificationType.InvoiceEmailNeedsReview,
                    "Email bez faktury",
                    $"Email od {payload.FromAddress} neobsahuje rozpoznatelnou fakturu.",
                    email.Id, companyId, ct);

                return EInvoiceEmailStatus.Failed;
            }

            var importedCount = 0;
            var failedCount = 0;
            var docNumbers = new List<string>();

            foreach (var att in invoiceAttachments)
            {
                try
                {
                    var result = await ProcessSingleAttachmentAsync(
                        email, payload, att, companyId, ct);

                    if (result.createdId.HasValue)
                    {
                        importedCount++;
                        docNumbers.Add(result.docNumber ?? $"#{result.createdId}");

                        // Store first created ID on the email record for navigation
                        if (result.direction == EInvoiceDirection.Received && email.ReceivedInvoiceId == null)
                            email.ReceivedInvoiceId = result.createdId;
                        else if (result.direction == EInvoiceDirection.Issued && email.InvoiceId == null)
                            email.InvoiceId = result.createdId;

                        if (email.Direction == null)
                            email.Direction = result.direction;
                    }
                    else
                    {
                        failedCount++;
                    }
                }
                catch (Exception ex)
                {
                    failedCount++;
                    _logger.LogError(ex, "Failed to process attachment {FileName} from email {EmailId}",
                        att.FileName, email.Id);
                }
            }

            // Set overall email status
            if (importedCount > 0)
                email.Status = email.ClassificationConfidence.HasValue
                    && email.ClassificationConfidence.Value < ConfidenceThreshold
                    ? EInvoiceEmailStatus.NeedsReview
                    : EInvoiceEmailStatus.Imported;
            else
                email.Status = EInvoiceEmailStatus.Failed;

            if (failedCount > 0 && importedCount > 0)
                email.StatusError = $"{failedCount} of {invoiceAttachments.Count} attachments failed.";

            await _context.SaveChangesAsync(ct);

            // Notify — single notification summarizing all imports
            if (importedCount > 0)
            {
                var summary = importedCount == 1
                    ? $"Faktura {docNumbers.FirstOrDefault()} importována z emailu od {payload.FromAddress}."
                    : $"{importedCount} faktur importováno z emailu od {payload.FromAddress}: {string.Join(", ", docNumbers)}.";

                await NotifyAsync(
                    email.Status == EInvoiceEmailStatus.NeedsReview
                        ? ENotificationType.InvoiceEmailNeedsReview
                        : ENotificationType.InvoiceEmailImported,
                    importedCount == 1 ? "Faktura importována" : $"{importedCount} faktur importováno",
                    summary,
                    email.ReceivedInvoiceId ?? email.InvoiceId ?? email.Id,
                    companyId, ct,
                    email.ReceivedInvoiceId.HasValue ? "ReceivedInvoice"
                        : email.InvoiceId.HasValue ? "Invoice"
                        : "InboundInvoiceEmail");
            }

            _logger.LogInformation(
                "Invoice email batch processed: EmailId={EmailId} Imported={Imported} Failed={Failed}",
                email.Id, importedCount, failedCount);

            return email.Status;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to process invoice email {EmailId}", email.Id);
            email.Status = EInvoiceEmailStatus.Failed;
            email.StatusError = GetFullExceptionMessage(ex);
            await _context.SaveChangesAsync(ct);

            await NotifyAsync(ENotificationType.InvoiceEmailNeedsReview,
                "Import faktury selhal",
                $"Zpracování emailu od {payload.FromAddress} selhalo: {email.StatusError}",
                email.Id, companyId, ct, "InboundInvoiceEmail");

            return EInvoiceEmailStatus.Failed;
        }
    }

    // ─── Single attachment processing ──────────────────────────────────────

    private async Task<(long? createdId, EInvoiceDirection direction, string? docNumber)> ProcessSingleAttachmentAsync(
        InboundInvoiceEmail email,
        InvoiceEmailPayload payload,
        EmailAttachment att,
        long companyId,
        CancellationToken ct)
    {
        // Extract invoice data from this single attachment
        InvoiceExtractedData? data = null;

        if (IsIsdoc(att))
        {
            var xml = ExtractIsdocXml(att);
            if (xml != null)
                data = _isdocParser.Parse(xml);
        }
        else if (IsUbl(att))
        {
            // Pure XML deserialization, no AI — same trust level as ISDOC.
            // See UblImportParser for the XXE/DoS hardening applied here.
            data = _ublParser.Parse(att.Content);
        }
        else if (IsPdf(att))
        {
            try
            {
                var preview = await _importService.PreviewImportAsync(
                    att.Content, att.FileName, EImportTarget.ReceivedInvoice, ct);
                if (preview != null)
                    data = MapPreviewToExtractedData(preview);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "PDF extraction failed for {FileName}", att.FileName);
            }
        }

        if (data == null)
        {
            _logger.LogWarning("No invoice data extracted from {FileName}", att.FileName);
            return (null, EInvoiceDirection.Received, null);
        }

        // Classify direction
        var issuer = await _context.Client
            .AsNoTracking()
            .FirstOrDefaultAsync(c => c.IsIssuer, ct);
        var companyIco = issuer?.RegistrationNumber ?? "";

        EInvoiceDirection direction;
        if (!string.IsNullOrEmpty(data.IssuerRegistrationNumber) && !string.IsNullOrEmpty(companyIco))
        {
            direction = data.IssuerRegistrationNumber.Trim() == companyIco.Trim()
                ? EInvoiceDirection.Issued
                : EInvoiceDirection.Received;
            email.ClassificationConfidence = 1.0m;
        }
        else
        {
            var result = await _classifier.ClassifyAsync(
                payload.TextBody, null, null, companyIco,
                issuer?.CompanyName ?? "", companyId, ct);
            direction = result.Direction;
            email.ClassificationConfidence = result.Confidence;
            data.IssuerRegistrationNumber ??= result.IssuerRegistrationNumber;
            data.IssuerName ??= result.IssuerName;
        }

        // Duplicate detection
        var (existingId, existingEntityType) = await FindExistingInvoiceAsync(data, direction, ct);
        if (existingId.HasValue)
        {
            if (_fileAttachmentService != null)
                await AttachFilesAsync([att], existingEntityType!, existingId.Value, companyId, ct);

            _logger.LogInformation(
                "Attachment {FileName} matched existing {EntityType}/{EntityId}",
                att.FileName, existingEntityType, existingId);

            return (existingId, direction, data.DocumentNumber);
        }

        // Create invoice
        long? createdId;
        string entityType;

        if (direction == EInvoiceDirection.Received)
        {
            createdId = await CreateReceivedInvoiceAsync(data, ct);
            entityType = "ReceivedInvoice";
        }
        else
        {
            createdId = await CreateIssuedInvoiceAsync(data, ct);
            entityType = "Invoice";
        }

        // Attach file to created invoice
        if (createdId.HasValue && _fileAttachmentService != null)
            await AttachFilesAsync([att], entityType, createdId.Value, companyId, ct);

        _logger.LogInformation(
            "Created {EntityType}/{EntityId} from {FileName} (DocNum={DocNum})",
            entityType, createdId, att.FileName, data.DocumentNumber);

        return (createdId, direction, data.DocumentNumber);
    }

    // ─── Duplicate detection ─────────────────────────────────────────────

    /// <summary>
    /// Checks if an invoice with the same document number already exists.
    /// For received invoices: match on DocumentNumber + supplier IČO.
    /// For issued invoices: match on DocumentNumber.
    /// Returns (existingId, entityType) or (null, null) if no match.
    /// </summary>
    private async Task<(long? id, string? entityType)> FindExistingInvoiceAsync(
        InvoiceExtractedData data, EInvoiceDirection direction, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(data.DocumentNumber))
            return (null, null);

        if (direction == EInvoiceDirection.Received)
        {
            var query = _context.Set<ReceivedInvoice>()
                .AsNoTracking()
                .Where(r => r.DocumentNumber == data.DocumentNumber);

            // Narrow by supplier IČO if available
            if (!string.IsNullOrEmpty(data.IssuerRegistrationNumber))
            {
                query = query.Where(r => r.Supplier != null
                    && r.Supplier.RegistrationNumber == data.IssuerRegistrationNumber);
            }

            var existing = await query.Select(r => r.Id).FirstOrDefaultAsync(ct);
            return existing > 0 ? (existing, "ReceivedInvoice") : (null, null);
        }
        else
        {
            var existing = await _context.Invoice
                .AsNoTracking()
                .Where(i => i.DocumentNumber == data.DocumentNumber
                         && i.Status != EInvoiceStatus.Deleted)
                .Select(i => i.Id)
                .FirstOrDefaultAsync(ct);

            return existing > 0 ? (existing, "Invoice") : (null, null);
        }
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
        var issuer = await _context.Client
            .AsNoTracking()
            .FirstOrDefaultAsync(c => c.IsIssuer, ct);

        if (issuer == null)
        {
            _logger.LogWarning("No issuer found in tenant — cannot create issued invoice");
            return null;
        }

        // Find or create recipient client
        long? clientId = null;
        if (!string.IsNullOrEmpty(data.RecipientRegistrationNumber))
        {
            var existing = await _context.Client
                .AsNoTracking()
                .FirstOrDefaultAsync(c => c.RegistrationNumber == data.RecipientRegistrationNumber
                                       && !c.IsIssuer, ct);

            if (existing != null)
            {
                clientId = existing.Id;
            }
            else
            {
                var created = await _clientService.CreateClientAsync(new Contracts.Dto.Client.CreateClientDto
                {
                    RegistrationNumber = data.RecipientRegistrationNumber,
                    CompanyName = data.RecipientName ?? $"Imported — {data.RecipientRegistrationNumber}",
                }, ct);
                clientId = created.Id;
            }
        }

        if (clientId == null)
        {
            _logger.LogWarning("No recipient IČO found — cannot create issued invoice without client");
            return null;
        }

        var currencyCode = data.Currency ?? "CZK";
        var currency = await _context.Currency
            .AsNoTracking()
            .FirstOrDefaultAsync(c => c.Code == currencyCode, ct);

        var docType = ResolveDocumentType(data.DetectedDocumentType);

        var items = data.Items?.Select(i => new CreateInvoiceItemDto
        {
            Description = i.Description ?? "Položka",
            Quantity = i.Quantity ?? 1m,
            UnitPrice = i.UnitPrice ?? 0m,
            Unit = i.Unit ?? "ks",
        }).ToList();

        if (items == null || items.Count == 0)
        {
            items =
            [
                new CreateInvoiceItemDto
                {
                    Description = data.DocumentNumber != null
                        ? $"Faktura {data.DocumentNumber}"
                        : "Položka faktury",
                    Quantity = 1m,
                    UnitPrice = data.TotalBeforeVat ?? data.TotalAmount ?? 0m,
                    Unit = "ks",
                }
            ];
        }

        var dto = new CreateInvoiceDto
        {
            DocumentType = docType,
            ClientId = clientId.Value,
            IssuerId = issuer.Id,
            IssueDate = data.IssueDate,
            DueDate = data.DueDate,
            TaxableSupplyDate = data.TaxableSupplyDate,
            VariableSymbol = data.VariableSymbol,
            BankAccountNumber = data.BankAccountNumber,
            IBAN = data.IBAN,
            SWIFT = data.SWIFT,
            CurrencyId = currency?.Id ?? 1,
            InvoiceItem = items,
        };

        var result = await _invoiceService.CreateInvoiceAsync(dto, ct);
        return result?.Id;
    }

    // ─── File attachment ─────────────────────────────────────────────────

    private async Task<int> AttachFilesAsync(
        IReadOnlyList<EmailAttachment> attachments,
        string entityType,
        long entityId,
        long companyId,
        CancellationToken ct)
    {
        var attached = 0;
        foreach (var att in attachments.Where(a => IsPdf(a) || IsIsdoc(a) || IsUbl(a)))
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
                        }, companyId, ct);
                    attached++;
                    _logger.LogInformation("Attached {FileName} to {EntityType}/{EntityId}",
                        att.FileName, entityType, entityId);
                }
                else
                {
                    _logger.LogWarning("IFileAttachmentService not available — skipping attachment {FileName}", att.FileName);
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to attach file {FileName} to {EntityType}/{EntityId}: {Error}",
                    att.FileName, entityType, entityId, GetFullExceptionMessage(ex));
            }
        }
        return attached;
    }

    // ─── Notifications ───────────────────────────────────────────────────

    private async Task NotifyAsync(
        ENotificationType type, string title, string message,
        long entityId, long companyId, CancellationToken ct, string entityType = "InboundInvoiceEmail")
    {
        try
        {
            await _notificationService.CreateForAllUsersAsync(type, title, message, entityId, entityType, companyId, ct);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to send notification for invoice email");
        }
    }

    // ─── Helpers ─────────────────────────────────────────────────────────

    private static string ComputeDeduplicationHash(
        long mailboxId, string messageId, string? imapUid)
    {
        var input = $"{mailboxId}|{messageId}|{imapUid}";
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(input));
        return Convert.ToHexStringLower(bytes);
    }

    private static string GetFullExceptionMessage(Exception ex)
    {
        var parts = new List<string>();
        var current = ex;
        while (current != null)
        {
            parts.Add(current.Message);
            current = current.InnerException;
        }
        var full = string.Join(" → ", parts);
        return full.Length > 2000 ? full[..2000] : full;
    }

    private static string? Truncate(string? value, int maxLength)
        => value != null && value.Length > maxLength ? value[..maxLength] : value;

    /// <summary>
    /// Infers document type from document number prefix when AI/ISDOC type is unavailable.
    /// </summary>
    private static string? InferDocumentTypeFromNumber(string? docNumber)
    {
        if (string.IsNullOrWhiteSpace(docNumber)) return null;
        var upper = docNumber.TrimStart().ToUpperInvariant();
        if (upper.StartsWith("CN") || upper.StartsWith("D-") || upper.Contains("DOBROPIS"))
            return "CreditNote";
        if (upper.StartsWith("PF") || upper.Contains("PROFORMA") || upper.Contains("ZÁLOHO"))
            return "Proforma";
        if (upper.StartsWith("DPP"))
            return "TaxReceiptForAdvance";
        return null;
    }

    private static EDocumentType ResolveDocumentType(string? detected) => detected?.ToLowerInvariant() switch
    {
        "creditnote" => EDocumentType.CreditNote,
        "proforma" => EDocumentType.Proforma,
        "taxreceiptforadvance" => EDocumentType.TaxReceiptForAdvance,
        _ => EDocumentType.Invoice
    };

    private static bool IsPdf(EmailAttachment att)
        => att.ContentType.Contains("pdf", StringComparison.OrdinalIgnoreCase)
        || att.FileName.EndsWith(".pdf", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Extracts ISDOC XML from an attachment. Handles both plain .isdoc (XML)
    /// and .isdocx (ZIP container with .isdoc inside).
    /// </summary>
    private string? ExtractIsdocXml(EmailAttachment att)
    {
        // ZIP files start with PK header (0x504B0304)
        if (att.Content.Length >= 4
            && att.Content[0] == 0x50 && att.Content[1] == 0x4B
            && att.Content[2] == 0x03 && att.Content[3] == 0x04)
        {
            try
            {
                using var zipStream = new MemoryStream(att.Content);
                using var archive = new System.IO.Compression.ZipArchive(zipStream, System.IO.Compression.ZipArchiveMode.Read);

                var isdocEntry = archive.Entries
                    .FirstOrDefault(e => e.Name.EndsWith(".isdoc", StringComparison.OrdinalIgnoreCase));

                if (isdocEntry == null)
                {
                    _logger.LogDebug("ISDOCX ZIP contains no .isdoc file");
                    return null;
                }

                using var entryStream = isdocEntry.Open();
                using var reader = new StreamReader(entryStream, Encoding.UTF8);
                return reader.ReadToEnd();
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to extract ISDOC from ZIP container");
                return null;
            }
        }

        // Plain .isdoc XML
        return Encoding.UTF8.GetString(att.Content);
    }

    private static bool IsIsdoc(EmailAttachment att)
        => att.FileName.EndsWith(".isdoc", StringComparison.OrdinalIgnoreCase)
        || att.FileName.EndsWith(".isdocx", StringComparison.OrdinalIgnoreCase)
        || att.ContentType.Contains("isdoc", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Detects a UBL/Peppol BIS attachment (F1.10) by file extension or content type.
    /// A false positive (some unrelated .xml attachment) is harmless: UblImportParser.Parse
    /// returns null for anything that isn't a recognized Invoice/CreditNote root, and this
    /// attachment is then simply counted as "no invoice data extracted" like any other
    /// unreadable attachment — it never crashes the batch.
    /// </summary>
    private static bool IsUbl(EmailAttachment att)
        => att.FileName.EndsWith(".xml", StringComparison.OrdinalIgnoreCase)
        || (att.ContentType.Contains("xml", StringComparison.OrdinalIgnoreCase) && !IsIsdoc(att));

    private static InvoiceExtractedData MapPreviewToExtractedData(
        Contracts.Dto.Import.InvoiceImportPreviewDto p) => new()
    {
        DocumentNumber = p.DocumentNumber,
        DetectedDocumentType = InferDocumentTypeFromNumber(p.DocumentNumber),
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

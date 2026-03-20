using Fakvio.Application.QrPayment;
using Fakvio.Application.Service;
using Fakvio.Contracts.Dto.Client;
using Fakvio.Contracts.Dto.Import;
using Fakvio.Contracts.Dto.Invoice;
using Fakvio.Contracts.Dto.ReceivedInvoice;
using Fakvio.Domain.Enums;
using Fakvio.Infrastructure.Data;
using Microsoft.Extensions.Logging;

namespace Fakvio.Infrastructure.Service;

/// <summary>
/// Orchestrates the invoice import process from PDF files.
///
/// The import uses a 3-tier waterfall extraction pipeline:
/// 1. QR Code (SIND/SPD) — fastest, most reliable, structured data from QR image
/// 2. AI Extraction — sends PDF text to AI provider, returns structured JSON
/// 3. Regex Fallback — pattern matching on PDF text, least reliable
///
/// Each level fills in fields that previous levels couldn't extract.
/// When multiple levels produce data, QR takes priority, then AI, then regex.
///
/// The import follows a 2-step workflow:
/// 1. Preview: Extract → Validate → Show to user
/// 2. Confirm: User edits → Create invoices
/// </summary>
public class InvoiceImportService : IInvoiceImportService
{
    private readonly TenantDbContext _context;
    private readonly IQrCodeExtractor _qrExtractor;
    private readonly IInvoiceAiExtractor _aiExtractor;
    private readonly IInvoiceTextExtractor _textExtractor;
    private readonly IPdfTextExtractorService _pdfReader;
    private readonly IClientService _clientService;
    private readonly IInvoiceService _invoiceService;
    private readonly IReceivedInvoiceService _receivedInvoiceService;
    private readonly ICurrencyService _currencyService;
    private readonly ILogger<InvoiceImportService> _logger;

    public InvoiceImportService(
        TenantDbContext context,
        IQrCodeExtractor qrExtractor,
        IInvoiceAiExtractor aiExtractor,
        IInvoiceTextExtractor textExtractor,
        IPdfTextExtractorService pdfReader,
        IClientService clientService,
        IInvoiceService invoiceService,
        IReceivedInvoiceService receivedInvoiceService,
        ICurrencyService currencyService,
        ILogger<InvoiceImportService> logger)
    {
        _context = context;
        _qrExtractor = qrExtractor;
        _aiExtractor = aiExtractor;
        _textExtractor = textExtractor;
        _pdfReader = pdfReader;
        _clientService = clientService;
        _invoiceService = invoiceService;
        _receivedInvoiceService = receivedInvoiceService;
        _currencyService = currencyService;
        _logger = logger;
    }

    // ─── Preview ─────────────────────────────────────────────────────────

    /// <summary>
    /// Extracts data from a PDF using the 3-tier pipeline, validates it,
    /// and returns a preview DTO for user review.
    /// </summary>
    public async Task<InvoiceImportPreviewDto> PreviewImportAsync(
        byte[] pdfBytes, string fileName, EImportTarget target, CancellationToken ct = default)
    {
        _logger.LogInformation("Starting import preview for {FileName}, target: {Target}", fileName, target);

        // Step 1: Extract raw text from PDF (needed by Level 2 and Level 3)
        string pdfText;
        try
        {
            pdfText = await _pdfReader.ExtractTextAsync(pdfBytes);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to extract text from PDF {FileName}", fileName);
            return CreateErrorPreview(fileName, "Failed to read PDF: " + ex.Message);
        }

        // Step 2: Level 1 — QR code extraction (fastest, most reliable)
        var qrResult = await _qrExtractor.ExtractFromPdfAsync(pdfBytes);
        InvoiceExtractedData? data = null;

        if (qrResult.Found && qrResult.Sind != null)
        {
            data = qrResult.ToExtractedData();
            _logger.LogInformation("QR code found ({Type}) in {FileName}", qrResult.QrType, fileName);
        }

        // Step 3: Level 2 — AI extraction (if QR insufficient or not found).
        // Pass company context so AI knows which company is importing and can:
        // - Distinguish issuer vs recipient correctly
        // - Preserve document number exactly for issued invoices
        InvoiceExtractedData? aiData = null;
        var companyId = ParseCompanyIdFromSchema(_context.Schema);
        if (!string.IsNullOrWhiteSpace(pdfText))
        {
            // Build company context for AI — issuer info + import direction.
            var companyContext = await BuildCompanyContextAsync(target, ct);
            aiData = await _aiExtractor.ExtractAsync(companyId, pdfText, companyContext, ct);
            if (aiData != null)
            {
                _logger.LogInformation("AI extraction successful for {FileName}", fileName);
            }
        }

        // Step 4: Level 3 — Regex fallback
        var regexData = _textExtractor.Extract(pdfText);

        // Step 5: Merge data — QR > AI > Regex priority
        data = MergeExtractedData(data, aiData, regexData);

        // Step 6: Build preview with validation
        var preview = await BuildPreview(data, fileName, qrResult, target, ct);

        _logger.LogInformation(
            "Import preview for {FileName}: Source={Source}, Valid={Valid}, Validations={Count}",
            fileName, data.Source, preview.IsValid, preview.Validations.Count);

        return preview;
    }

    // ─── Confirm ─────────────────────────────────────────────────────────

    /// <summary>
    /// Creates invoices from user-confirmed import data.
    /// Handles client creation and delegates to the appropriate invoice service.
    /// </summary>
    public async Task<List<ImportResultDto>> ConfirmImportAsync(
        ConfirmInvoiceImportRequest request, CancellationToken ct = default)
    {
        var results = new List<ImportResultDto>();

        foreach (var item in request.Items)
        {
            var result = new ImportResultDto { FileName = item.FileName };

            try
            {
                // Create client if needed
                long? clientId = item.ClientId;
                if (item.CreateClient && !string.IsNullOrWhiteSpace(item.NewClientRegistrationNumber))
                {
                    var newClient = await _clientService.CreateClientAsync(new CreateClientDto
                    {
                        RegistrationNumber = item.NewClientRegistrationNumber,
                        CompanyName = "Imported client — " + item.NewClientRegistrationNumber
                    }, ct);
                    clientId = newClient.Id;
                    result.CreatedClientId = newClient.Id;
                    _logger.LogInformation("Created client {Id} for import ({FileName})", newClient.Id, item.FileName);
                }

                if (clientId == null || clientId == 0)
                {
                    result.Success = false;
                    result.ErrorMessage = "No client specified and client creation not requested.";
                    results.Add(result);
                    continue;
                }

                // Resolve currency
                var currencyId = await ResolveCurrencyIdAsync(item.Currency, ct);

                // Create the invoice based on target type
                if (request.Target == EImportTarget.IssuedInvoice)
                {
                    var invoice = await CreateIssuedInvoice(item, clientId.Value, currencyId, ct);
                    result.Success = true;
                    result.InvoiceId = invoice.Id;
                    result.DocumentNumber = invoice.DocumentNumber;
                }
                else
                {
                    var invoice = await CreateReceivedInvoice(item, clientId.Value, currencyId, ct);
                    result.Success = true;
                    result.InvoiceId = invoice.Id;
                    result.DocumentNumber = invoice.DocumentNumber;
                }

                _logger.LogInformation("Imported invoice {DocNum} from {FileName}", result.DocumentNumber, item.FileName);
            }
            catch (Exception ex)
            {
                result.Success = false;
                result.ErrorMessage = ex.Message;
                _logger.LogWarning(ex, "Failed to import {FileName}: {Message}", item.FileName, ex.Message);
            }

            results.Add(result);
        }

        return results;
    }

    // ─── Data merging ────────────────────────────────────────────────────

    /// <summary>
    /// Merges extracted data from multiple sources with QR > AI > Regex priority.
    /// Each field is taken from the highest-priority source that has it.
    /// </summary>
    internal static InvoiceExtractedData MergeExtractedData(
        InvoiceExtractedData? qrData,
        InvoiceExtractedData? aiData,
        InvoiceExtractedData? regexData)
    {
        // Start with the best available base
        var result = qrData ?? aiData ?? regexData ?? new InvoiceExtractedData();

        // If we have multiple sources, merge missing fields
        if (qrData != null && (aiData != null || regexData != null))
        {
            // QR is base — fill gaps from AI, then regex
            FillMissingFields(result, aiData);
            FillMissingFields(result, regexData);
            result.Source = EExtractionSource.Merged;
        }
        else if (aiData != null && regexData != null)
        {
            // AI is base — fill gaps from regex
            FillMissingFields(result, regexData);
            result.Source = EExtractionSource.Merged;
        }

        return result;
    }

    /// <summary>
    /// Fills null fields in the target from the source.
    /// Does not overwrite existing non-null values.
    /// </summary>
    private static void FillMissingFields(InvoiceExtractedData target, InvoiceExtractedData? source)
    {
        if (source == null) return;

        target.DocumentNumber ??= source.DocumentNumber;
        target.IssueDate ??= source.IssueDate;
        target.DueDate ??= source.DueDate;
        target.TaxableSupplyDate ??= source.TaxableSupplyDate;
        target.TotalAmount ??= source.TotalAmount;
        target.TotalVat ??= source.TotalVat;
        target.TotalBeforeVat ??= source.TotalBeforeVat;
        target.Currency ??= source.Currency;
        target.VariableSymbol ??= source.VariableSymbol;
        target.IBAN ??= source.IBAN;
        target.SWIFT ??= source.SWIFT;
        target.BankAccountNumber ??= source.BankAccountNumber;
        target.PaymentMethod ??= source.PaymentMethod;
        target.IssuerName ??= source.IssuerName;
        target.IssuerRegistrationNumber ??= source.IssuerRegistrationNumber;
        target.IssuerTaxNumber ??= source.IssuerTaxNumber;
        target.RecipientName ??= source.RecipientName;
        target.RecipientRegistrationNumber ??= source.RecipientRegistrationNumber;
        target.RecipientTaxNumber ??= source.RecipientTaxNumber;
        target.Items ??= source.Items;
    }

    // ─── Preview building ────────────────────────────────────────────────

    /// <summary>
    /// Builds the preview DTO from extracted data, including validation.
    /// </summary>
    private async Task<InvoiceImportPreviewDto> BuildPreview(
        InvoiceExtractedData data,
        string fileName,
        QrExtractionResult qrResult,
        EImportTarget target,
        CancellationToken ct)
    {
        var preview = new InvoiceImportPreviewDto
        {
            FileName = fileName,
            HasQrCode = qrResult.Found,
            QrType = qrResult.QrType,
            ExtractionSource = data.Source.ToString(),

            // Copy extracted data to preview
            DocumentNumber = data.DocumentNumber,
            IssueDate = data.IssueDate,
            DueDate = data.DueDate,
            TaxableSupplyDate = data.TaxableSupplyDate,
            TotalAmount = data.TotalAmount,
            TotalVat = data.TotalVat,
            TotalBeforeVat = data.TotalBeforeVat,
            Currency = data.Currency,
            VariableSymbol = data.VariableSymbol,
            IBAN = data.IBAN,
            SWIFT = data.SWIFT,
            BankAccountNumber = data.BankAccountNumber,
            PaymentMethod = data.PaymentMethod,
            IssuerName = data.IssuerName,
            IssuerRegistrationNumber = data.IssuerRegistrationNumber,
            IssuerTaxNumber = data.IssuerTaxNumber,
            RecipientName = data.RecipientName,
            RecipientRegistrationNumber = data.RecipientRegistrationNumber,
            RecipientTaxNumber = data.RecipientTaxNumber,

            // Map extracted items to DTO
            Items = data.Items?.Select(i => new ImportInvoiceItemDto
            {
                Description = i.Description,
                Quantity = i.Quantity,
                UnitPrice = i.UnitPrice,
                VatRate = i.VatRate,
                Unit = i.Unit
            }).ToList()
        };

        // ── Validate ─────────────────────────────────────────────────────
        await ValidatePreview(preview, target, ct);

        return preview;
    }

    /// <summary>
    /// Runs all validation rules against the preview data.
    /// Adds validation messages (Info/Warning/Error) to the preview.
    /// </summary>
    private async Task ValidatePreview(
        InvoiceImportPreviewDto preview, EImportTarget target, CancellationToken ct)
    {
        // 1. Required fields
        if (preview.TotalAmount == null || preview.TotalAmount == 0)
        {
            preview.Validations.Add(new ImportValidationMessage
            {
                Field = "TotalAmount",
                Message = "Total amount is missing or zero.",
                Severity = EImportValidationSeverity.Error
            });
        }

        // 2. Get the active company (issuer) for role validation
        var issuer = await _clientService.GetIssuerAsync(ct);

        // 3. Target-specific validation
        if (target == EImportTarget.IssuedInvoice)
        {
            await ValidateIssuedInvoice(preview, issuer, ct);
        }
        else
        {
            await ValidateReceivedInvoice(preview, issuer, ct);
        }
    }

    /// <summary>
    /// Validates an issued invoice import:
    /// - Issuer on the PDF must match the active company
    /// - Recipient (client) must exist or be creatable
    /// - Document number duplicate check
    /// </summary>
    private async Task ValidateIssuedInvoice(
        InvoiceImportPreviewDto preview,
        Fakvio.Contracts.Dto.Client.ClientDto? issuer,
        CancellationToken ct)
    {
        // Issuer check: the invoice's issuer must be our company
        if (!string.IsNullOrWhiteSpace(preview.IssuerRegistrationNumber) && issuer != null)
        {
            if (preview.IssuerRegistrationNumber != issuer.RegistrationNumber)
            {
                preview.Validations.Add(new ImportValidationMessage
                {
                    Field = "IssuerRegistrationNumber",
                    Message = $"Invoice issuer IČO ({preview.IssuerRegistrationNumber}) " +
                              $"does not match your company ({issuer.RegistrationNumber}).",
                    Severity = EImportValidationSeverity.Error
                });
            }
        }

        // Resolve recipient (client) by IČO
        if (!string.IsNullOrWhiteSpace(preview.RecipientRegistrationNumber))
        {
            var client = await _clientService.GetClientByRegistrationNumberAsync(
                preview.RecipientRegistrationNumber, ct);

            if (client != null)
            {
                preview.ResolvedClientId = client.Id;
                preview.ResolvedClientName = client.CompanyName;
                preview.Validations.Add(new ImportValidationMessage
                {
                    Field = "Client",
                    Message = $"Client found: {client.CompanyName} (IČO: {client.RegistrationNumber})",
                    Severity = EImportValidationSeverity.Info
                });
            }
            else
            {
                preview.ClientNeedsCreation = true;
                preview.Validations.Add(new ImportValidationMessage
                {
                    Field = "Client",
                    Message = $"Client with IČO {preview.RecipientRegistrationNumber} not found. " +
                              "A new client will be created on import.",
                    Severity = EImportValidationSeverity.Warning
                });
            }
        }
        else
        {
            preview.Validations.Add(new ImportValidationMessage
            {
                Field = "Client",
                Message = "Recipient IČO not detected. Please select a client manually.",
                Severity = EImportValidationSeverity.Warning
            });
        }

        // Document number duplicate check for issued invoices
        if (!string.IsNullOrWhiteSpace(preview.DocumentNumber))
        {
            var existing = await _invoiceService.GetAllInvoicesAsync(
                documentType: EDocumentType.Invoice, cancellationToken: ct);
            if (existing.Any(i => i.DocumentNumber == preview.DocumentNumber))
            {
                preview.Validations.Add(new ImportValidationMessage
                {
                    Field = "DocumentNumber",
                    Message = $"Invoice with number '{preview.DocumentNumber}' already exists.",
                    Severity = EImportValidationSeverity.Error
                });
            }
        }
    }

    /// <summary>
    /// Validates a received invoice import:
    /// - Recipient on the PDF must match the active company
    /// - Supplier (issuer) must exist or be creatable
    /// - Document number + supplier duplicate check
    /// </summary>
    private async Task ValidateReceivedInvoice(
        InvoiceImportPreviewDto preview,
        Fakvio.Contracts.Dto.Client.ClientDto? issuer,
        CancellationToken ct)
    {
        // Recipient check: the invoice's recipient must be our company
        if (!string.IsNullOrWhiteSpace(preview.RecipientRegistrationNumber) && issuer != null)
        {
            if (preview.RecipientRegistrationNumber != issuer.RegistrationNumber)
            {
                preview.Validations.Add(new ImportValidationMessage
                {
                    Field = "RecipientRegistrationNumber",
                    Message = $"Invoice recipient IČO ({preview.RecipientRegistrationNumber}) " +
                              $"does not match your company ({issuer.RegistrationNumber}).",
                    Severity = EImportValidationSeverity.Error
                });
            }
        }

        // Resolve supplier by IČO
        if (!string.IsNullOrWhiteSpace(preview.IssuerRegistrationNumber))
        {
            var supplier = await _clientService.GetClientByRegistrationNumberAsync(
                preview.IssuerRegistrationNumber, ct);

            if (supplier != null)
            {
                preview.ResolvedClientId = supplier.Id;
                preview.ResolvedClientName = supplier.CompanyName;
                preview.Validations.Add(new ImportValidationMessage
                {
                    Field = "Supplier",
                    Message = $"Supplier found: {supplier.CompanyName} (IČO: {supplier.RegistrationNumber})",
                    Severity = EImportValidationSeverity.Info
                });

                // Duplicate check: same document number from same supplier
                if (!string.IsNullOrWhiteSpace(preview.DocumentNumber))
                {
                    var existing = await _receivedInvoiceService.GetAllAsync(ct: ct);
                    if (existing.Any(i =>
                        i.DocumentNumber == preview.DocumentNumber && i.SupplierId == supplier.Id))
                    {
                        preview.Validations.Add(new ImportValidationMessage
                        {
                            Field = "DocumentNumber",
                            Message = $"Received invoice '{preview.DocumentNumber}' from this supplier already exists.",
                            Severity = EImportValidationSeverity.Error
                        });
                    }
                }
            }
            else
            {
                preview.ClientNeedsCreation = true;
                preview.Validations.Add(new ImportValidationMessage
                {
                    Field = "Supplier",
                    Message = $"Supplier with IČO {preview.IssuerRegistrationNumber} not found. " +
                              "A new client will be created on import.",
                    Severity = EImportValidationSeverity.Warning
                });
            }
        }
        else
        {
            preview.Validations.Add(new ImportValidationMessage
            {
                Field = "Supplier",
                Message = "Supplier IČO not detected. Please select a supplier manually.",
                Severity = EImportValidationSeverity.Warning
            });
        }
    }

    // ─── Invoice creation helpers ────────────────────────────────────────

    private async Task<Fakvio.Contracts.Dto.Invoice.InvoiceDto> CreateIssuedInvoice(
        ConfirmImportItemDto item, long clientId, long currencyId, CancellationToken ct)
    {
        var issuer = await _clientService.GetIssuerAsync(ct)
            ?? throw new InvalidOperationException("No issuer (company) configured.");

        var createDto = new CreateInvoiceDto
        {
            DocumentType = EDocumentType.Invoice,
            ClientId = clientId,
            IssuerId = issuer.Id,
            IssueDate = item.IssueDate,
            DueDate = item.DueDate,
            TaxableSupplyDate = item.TaxableSupplyDate,
            VariableSymbol = item.VariableSymbol,
            BankAccountNumber = item.BankAccountNumber,
            IBAN = item.IBAN,
            SWIFT = item.SWIFT,
            CurrencyId = currencyId,
            CustomDocumentNumber = item.DocumentNumber,
            Notes = "Imported from PDF: " + item.FileName,
            // At least one item is required — use extracted items or a fallback summary line
            InvoiceItem = BuildInvoiceItems(item)
        };

        return await _invoiceService.CreateInvoiceAsync(createDto, ct);
    }

    private async Task<Fakvio.Contracts.Dto.ReceivedInvoice.ReceivedInvoiceDto> CreateReceivedInvoice(
        ConfirmImportItemDto item, long supplierId, long currencyId, CancellationToken ct)
    {
        var createDto = new CreateReceivedInvoiceDto
        {
            DocumentNumber = item.DocumentNumber,
            SupplierId = supplierId,
            IssueDate = item.IssueDate,
            DueDate = item.DueDate,
            TaxableSupplyDate = item.TaxableSupplyDate,
            VariableSymbol = item.VariableSymbol,
            BankAccountNumber = item.BankAccountNumber,
            IBAN = item.IBAN,
            SWIFT = item.SWIFT,
            CurrencyId = currencyId,
            Notes = "Imported from PDF: " + item.FileName,
            Items = BuildReceivedInvoiceItems(item)
        };

        return await _receivedInvoiceService.CreateAsync(createDto, ct);
    }

    /// <summary>
    /// Builds invoice items from the confirmed import data.
    /// If no items were extracted (AI-only feature), creates a single summary line.
    /// </summary>
    private static List<CreateInvoiceItemDto> BuildInvoiceItems(ConfirmImportItemDto item)
    {
        if (item.Items is { Count: > 0 })
        {
            return item.Items.Select((i, idx) => new CreateInvoiceItemDto
            {
                OrderIndex = idx + 1,
                Description = i.Description ?? "Imported item",
                Quantity = i.Quantity ?? 1,
                UnitPrice = i.UnitPrice ?? 0,
                Unit = i.Unit ?? "pcs",
                VatRatePercentage = i.VatRate ?? 0
            }).ToList();
        }

        // Fallback: single summary line with the total amount
        return new List<CreateInvoiceItemDto>
        {
            new()
            {
                OrderIndex = 1,
                Description = "Imported invoice — see PDF attachment for details",
                Quantity = 1,
                UnitPrice = item.TotalBeforeVat ?? item.TotalAmount ?? 0,
                Unit = "pcs",
                VatRatePercentage = 0
            }
        };
    }

    /// <summary>
    /// Builds received invoice items from the confirmed import data.
    /// </summary>
    private static List<CreateReceivedInvoiceItemDto> BuildReceivedInvoiceItems(ConfirmImportItemDto item)
    {
        if (item.Items is { Count: > 0 })
        {
            return item.Items.Select((i, idx) => new CreateReceivedInvoiceItemDto
            {
                OrderIndex = idx + 1,
                Description = i.Description ?? "Imported item",
                Quantity = i.Quantity ?? 1,
                UnitPrice = i.UnitPrice ?? 0,
                Unit = i.Unit ?? "pcs",
                VatRatePercentage = i.VatRate ?? 0
            }).ToList();
        }

        return new List<CreateReceivedInvoiceItemDto>
        {
            new()
            {
                OrderIndex = 1,
                Description = "Imported invoice — see PDF attachment for details",
                Quantity = 1,
                UnitPrice = item.TotalBeforeVat ?? item.TotalAmount ?? 0,
                Unit = "pcs",
                VatRatePercentage = 0
            }
        };
    }

    /// <summary>
    /// Resolves a currency code (e.g., "CZK") to a currency ID.
    /// Falls back to CZK if the code is not found or not provided.
    /// </summary>
    private async Task<long> ResolveCurrencyIdAsync(string? currencyCode, CancellationToken ct)
    {
        if (!string.IsNullOrWhiteSpace(currencyCode))
        {
            var currency = await _currencyService.GetCurrencyByCodeAsync(currencyCode, ct);
            if (currency != null) return currency.Id;
        }

        // Default to CZK
        var czk = await _currencyService.GetCurrencyByCodeAsync("CZK", ct);
        return czk?.Id ?? 1; // Fallback to ID 1 if CZK doesn't exist (shouldn't happen)
    }

    /// <summary>
    /// Creates an error preview when PDF reading fails entirely.
    /// </summary>
    private static InvoiceImportPreviewDto CreateErrorPreview(string fileName, string error)
    {
        return new InvoiceImportPreviewDto
        {
            FileName = fileName,
            ExtractionSource = "Error",
            Validations =
            {
                new ImportValidationMessage
                {
                    Field = "PDF",
                    Message = error,
                    Severity = EImportValidationSeverity.Error
                }
            }
        };
    }

    /// <summary>
    /// Builds company context for the AI extractor — tells the AI which company is importing
    /// and whether this is an issued or received invoice import.
    /// Returns null if issuer info is not available (AI falls back to guessing from context).
    /// </summary>
    private async Task<ImportCompanyContext?> BuildCompanyContextAsync(
        EImportTarget target, CancellationToken ct)
    {
        try
        {
            var issuer = await _clientService.GetIssuerAsync(ct);
            if (issuer == null)
                return null;

            return new ImportCompanyContext(
                CompanyName: issuer.CompanyName,
                RegistrationNumber: issuer.RegistrationNumber,
                TaxNumber: issuer.TaxNumber,
                IsIssuedImport: target == EImportTarget.IssuedInvoice);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to load issuer for AI context — AI will guess from document content");
            return null;
        }
    }

    /// <summary>
    /// Extracts CompanyId from the tenant schema name (e.g., "tenant_42" → 42).
    /// Used to pass companyId to AI extractor for company-specific provider resolution.
    /// </summary>
    private static long? ParseCompanyIdFromSchema(string? schema)
    {
        if (string.IsNullOrEmpty(schema) || !schema.StartsWith("tenant_"))
            return null;

        return long.TryParse(schema.AsSpan("tenant_".Length), out var id) ? id : null;
    }
}

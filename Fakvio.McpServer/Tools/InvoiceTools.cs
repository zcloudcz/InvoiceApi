using System.ComponentModel;
using System.Text.Json;
using Fakvio.Contracts.Dto.Client;
using Fakvio.Contracts.Dto.Email;
using Fakvio.Contracts.Dto.Invoice;
using Fakvio.Domain.Enums;
using Fakvio.McpServer.Client;
using ModelContextProtocol.Server;

namespace Fakvio.McpServer.Tools;

/// <summary>
/// MCP tools for managing invoices and credit notes.
/// Each method is a standalone tool that an AI client can invoke.
///
/// Junior note: [McpServerToolType] marks this class for auto-discovery.
/// [McpServerTool] marks each method as an invocable tool.
/// The MCP SDK injects IFakvioApiClient from DI automatically.
/// </summary>
[McpServerToolType]
public static class InvoiceTools
{
    /// <summary>
    /// Shared JSON options for deserializing tool input and serializing output.
    /// CamelCase matches the API's JSON convention.
    /// JsonStringEnumConverter allows AI models to pass enum values as strings
    /// (e.g., "Invoice" instead of 1) which is more natural for AI interaction.
    /// </summary>
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        WriteIndented = true,
        Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter() }
    };

    /// <summary>
    /// Lists invoices with pagination and optional filters.
    /// Returns a page of invoices matching the given criteria.
    /// </summary>
    [McpServerTool(Title = "List invoices", ReadOnly = true, Idempotent = true, OpenWorld = false), Description(
        "List invoices with pagination and filters. " +
        "Returns paginated results with invoice details including status, amounts, and client info. " +
        "Sorted newest first (by issue date) unless sortBy is given.")]
    public static async Task<string> ListInvoices(
        IFakvioApiClient api,
        [Description("Page number (1-based, default 1)")] int page = 1,
        [Description("Items per page (default 20, max 100)")] int pageSize = 20,
        [Description("Search by document number, client name, or variable symbol")] string? search = null,
        [Description("Filter by document type: 'Invoice' or 'CreditNote'")] string? documentType = null,
        [Description("Filter by status: 'Draft', 'Completed', 'Paid', 'Creditnoted', or 'Deleted'")] string? status = null,
        [Description("Filter by client ID")] long? clientId = null,
        [Description("Filter invoices issued on or after this date (ISO 8601, e.g. '2026-01-01')")] string? issueDateFrom = null,
        [Description("Filter invoices issued on or before this date (ISO 8601)")] string? issueDateTo = null,
        [Description("Filter to only overdue invoices (true/false)")] bool? isOverdue = null,
        [Description("Sort field: DocumentNumber, IssueDate, DueDate, TaxableSupplyDate, TotalWithVat, Status, CreatedAt or UpdatedAt. Omit for newest first.")] string? sortBy = null,
        [Description("Sort direction for sortBy: 'asc' or 'desc' (default 'desc')")] string sortDirection = "desc",
        CancellationToken ct = default)
    {
        try
        {
            // Sorting is passed through as-is: the API validates sortBy and falls back
            // to its default (newest issue date first) when it is missing or unknown.
            var filter = new InvoiceFilterDto
            {
                Page = page,
                PageSize = Math.Min(pageSize, 100),
                Search = search,
                IsOverdue = isOverdue,
                SortBy = sortBy,
                SortDirection = sortDirection
            };

            // Parse enum strings safely — AI models may pass various casing
            if (!string.IsNullOrEmpty(documentType) && Enum.TryParse<EDocumentType>(documentType, ignoreCase: true, out var dt))
                filter.DocumentType = dt;

            if (!string.IsNullOrEmpty(status) && Enum.TryParse<EInvoiceStatus>(status, ignoreCase: true, out var st))
                filter.Status = st;

            if (clientId.HasValue)
                filter.ClientId = clientId.Value;

            if (DateTime.TryParse(issueDateFrom, out var dateFrom))
                filter.IssueDateFrom = dateFrom;

            if (DateTime.TryParse(issueDateTo, out var dateTo))
                filter.IssueDateTo = dateTo;

            var result = await api.GetInvoicesPagedAsync(filter, ct);
            return JsonSerializer.Serialize(result, JsonOptions);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            return McpToolError.ToJson(ex);
        }
    }

    /// <summary>
    /// Gets a single invoice by its database ID.
    /// Returns full invoice details including items, amounts, and metadata.
    /// </summary>
    [McpServerTool(Title = "Get invoice", ReadOnly = true, Idempotent = true, OpenWorld = false), Description(
        "Get a single invoice by ID. Returns full details including line items, " +
        "totals (before VAT, VAT, with VAT), payment status, and client/issuer info.")]
    public static async Task<string> GetInvoice(
        IFakvioApiClient api,
        [Description("The invoice ID (database primary key)")] long invoiceId,
        CancellationToken ct = default)
    {
        try
        {
            var invoice = await api.GetInvoiceByIdAsync(invoiceId, ct);

            if (invoice is null)
                return JsonSerializer.Serialize(new { error = $"Invoice with ID {invoiceId} not found." }, JsonOptions);

            return JsonSerializer.Serialize(invoice, JsonOptions);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            return McpToolError.ToJson(ex);
        }
    }

    /// <summary>
    /// Finds an invoice by its document number (e.g., "FAK2026001").
    /// Useful when the user refers to an invoice by its printed number.
    /// </summary>
    [McpServerTool(Title = "Find invoice by number", ReadOnly = true, Idempotent = true, OpenWorld = false), Description(
        "Find an invoice by its document number (e.g., 'FAK2026001'). " +
        "Use this when the user refers to an invoice by its printed/visible number.")]
    public static async Task<string> FindInvoiceByNumber(
        IFakvioApiClient api,
        [Description("The document number to search for (e.g., 'FAK2026001')")] string documentNumber,
        CancellationToken ct = default)
    {
        try
        {
            var invoice = await api.GetInvoiceByDocumentNumberAsync(documentNumber, ct);

            if (invoice is null)
                return JsonSerializer.Serialize(new { error = $"Invoice with document number '{documentNumber}' not found." }, JsonOptions);

            return JsonSerializer.Serialize(invoice, JsonOptions);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            return McpToolError.ToJson(ex);
        }
    }

    /// <summary>
    /// Creates a new invoice or credit note. The invoice starts in Draft status and must be
    /// completed (issued) separately via <see cref="CompleteInvoice"/>.
    ///
    /// Junior note (N2.4): this used to take a single "JSON string of CreateInvoiceDto"
    /// parameter with internal IDs (currencyId, issuerId) the AI model has no way to know.
    /// It is now typed parameters, and this method resolves the two lookups a model CAN
    /// reasonably provide — a currency code and a VAT percentage — into the internal IDs
    /// the API actually needs, entirely before any write call reaches the API.
    /// </summary>
    [McpServerTool(Title = "Create invoice", ReadOnly = false, Destructive = false, Idempotent = false, OpenWorld = false), Description(
        "Create a new invoice or credit note (Draft status). Recommended flow: " +
        "find_client / list_clients to get clientId → create_invoice → complete_invoice to issue it. " +
        "For a VAT-paying issuer, each non-text item only needs vatRatePercentage (e.g. 21) — " +
        "the matching VatRateId active on issueDate is resolved automatically. EU OSS is opt-in (applyOss=true) for an " +
        "OSS-registered issuer invoicing a consumer (client without a VAT id) in another EU state: vatRatePercentage " +
        "must then be a VAT rate of the client's country (rejected otherwise). " +
        "Bank account: when bankAccountId is omitted and the payment method is (or defaults to) " +
        "BankTransfer, the server automatically fills in one of the issuer's bank accounts " +
        "(currency-matching default first) — you don't need to set one for a normal invoice.")]
    public static async Task<string> CreateInvoice(
        IFakvioApiClient api,
        [Description("The client (customer) ID — find it with list_clients or find_client")] long clientId,
        [Description(
            "Line items. Each needs description, quantity, unit, unitPrice and (for a VAT-paying " +
            "issuer) vatRatePercentage (e.g. 21); vatRateId is resolved automatically from the " +
            "percentage, do not set it. Use isTextRow=true for a note-only line. " +
            "For a reverse charge item (PDP, §92a-92e ZDPH): set vatRegime to 'ReverseCharge' and " +
            "reverseChargeCodeId to the Id of a code from list_reverse_charge_codes — required " +
            "together, and only for ReverseCharge items.")]
        List<CreateInvoiceItemDto> items,
        [Description("'Invoice' or 'CreditNote' (default 'Invoice')")] string documentType = "Invoice",
        [Description("ISO 4217 currency code, e.g. 'EUR' — see list_currencies. Omit for CZK.")] string? currency = null,
        [Description("Issuer (your company) ID. Omit to use the authenticated user's own company (get_issuer).")] long? issuerId = null,
        [Description("Issue date, ISO 8601 (e.g. '2026-01-15'). Omit for today.")] string? issueDate = null,
        [Description("Due date, ISO 8601. Omit to use the client's billing settings.")] string? dueDate = null,
        [Description("Variable symbol (max 10 digits). Omit to auto-generate from the document number.")] string? variableSymbol = null,
        [Description("Payment method: BankTransfer, Cash, CreditCard, PayPal, Other. Omit for the client's default.")] string? paymentMethod = null,
        [Description("Optional notes on the invoice")] string? notes = null,
        [Description("For a credit note (documentType='CreditNote'): the ID of the invoice it corrects")] long? originalInvoiceId = null,
        [Description(
            "Apply the EU OSS regime (destination-country VAT). Default false. Set true only for supplies that really fall " +
            "under OSS (goods distance sales, telecom/broadcasting/electronic services, ...) — general B2C services such as " +
            "consulting are taxed in CZ. Requires an OSS-registered VAT-payer issuer and a consumer client in another EU state.")] bool applyOss = false,
        [Description(
            "Optional: one of the issuer's bank accounts (BankAccount.Id, see the bankAccount " +
            "list in get_issuer) to use instead of the automatic default. Must belong to the issuer.")]
        long? bankAccountId = null,
        CancellationToken ct = default)
    {
        // ── Validate the model's own input BEFORE any API call ──────────────
        // Deliberately kept out of the try/catch below (issue #279 — see McpToolError):
        // an unknown currency code or VAT percentage is the model's mistake, not the API's,
        // and reporting it precisely here means the API is never even called with bad data.

        if (!Enum.TryParse<EDocumentType>(documentType, ignoreCase: true, out var parsedDocumentType))
            return Error($"Unknown documentType '{documentType}'. Use 'Invoice' or 'CreditNote'.");

        EPaymentMethod? parsedPaymentMethod = null;
        if (!string.IsNullOrWhiteSpace(paymentMethod))
        {
            if (!Enum.TryParse<EPaymentMethod>(paymentMethod, ignoreCase: true, out var pm))
                return Error(
                    $"Unknown paymentMethod '{paymentMethod}'. Valid values: " +
                    string.Join(", ", Enum.GetNames<EPaymentMethod>()) + ".");
            parsedPaymentMethod = pm;
        }

        DateTime? parsedIssueDate = null;
        if (!string.IsNullOrWhiteSpace(issueDate))
        {
            if (!DateTime.TryParse(issueDate, out var d))
                return Error($"Invalid issueDate '{issueDate}'. Use ISO 8601 (e.g. '2026-01-15').");
            parsedIssueDate = d;
        }

        DateTime? parsedDueDate = null;
        if (!string.IsNullOrWhiteSpace(dueDate))
        {
            if (!DateTime.TryParse(dueDate, out var d))
                return Error($"Invalid dueDate '{dueDate}'. Use ISO 8601.");
            parsedDueDate = d;
        }

        try
        {
            // ── Resolve issuer (explicit ID or the authenticated user's own company) ────
            ClientDto? issuer = issuerId.HasValue
                ? await api.GetClientByIdAsync(issuerId.Value, ct)
                : await api.GetIssuerAsync(ct);

            if (issuer is null)
            {
                return Error(issuerId.HasValue
                    ? $"Issuer with ID {issuerId} not found."
                    : "No issuer (your company) is configured. Set one up first, or pass issuerId explicitly.");
            }

            // ── Resolve currency code → CurrencyId (default CZK) ────────────────
            var (resolvedCurrency, currencyError) = await CodeListTools.ResolveCurrencyAsync(api, currency, ct);
            if (resolvedCurrency is null)
                return Error(currencyError!);

            // ── Resolve VatRateId from VatRatePercentage — VAT payers only ──────
            // A non-VAT-payer issuer has no VAT rates to configure at all (readiness never
            // asks for one), so items are left exactly as the model sent them (same rule as
            // InvoiceService.CreateInvoiceAsync, which only demands VatRateId for VAT payers).
            // EU OSS (DEVGUIDE §4.16): when the invoice falls under OSS the server auto-detects it and
            // validates vatRatePercentage against the destination country's rates — the CZ rate table
            // must not be consulted (a German 19 % would be "no matching rate" there).
            var ossCountry = applyOss ? await api.GetOssCountryAsync(clientId, issuer.Id, parsedDocumentType, ct) : null;
            if (applyOss && string.IsNullOrEmpty(ossCountry))
                return Error("applyOss=true but this invoice is not eligible for OSS: it needs an OSS-registered VAT-payer issuer, " +
                             "an Invoice (not proforma/credit note) and a consumer client without VAT id in another EU state.");
            if (issuer.IsVatPayer && string.IsNullOrEmpty(ossCountry))
            {
                var itemsNeedingRate = items.Where(i => !i.IsTextRow && !i.VatRateId.HasValue).ToList();
                if (itemsNeedingRate.Count > 0)
                {
                    var activeRates = await api.GetActiveVatRatesAsync(parsedIssueDate, ct);

                    foreach (var item in itemsNeedingRate)
                    {
                        // Codex review: FirstOrDefault picked an arbitrary rate when a tenant has
                        // more than one active rate at the same percentage (e.g. two overlapping
                        // validity periods during a rate change) — the model never even saw that
                        // there was a choice. A single match still resolves silently; two or more
                        // is a real ambiguity the model must resolve itself, by setting vatRateId
                        // on the item directly (CreateInvoiceItemDto already has that property).
                        var candidates = activeRates.Where(r => r.Rate == item.VatRatePercentage).ToList();

                        if (candidates.Count == 0)
                        {
                            return Error(
                                $"No active VAT rate matches {item.VatRatePercentage}% " +
                                $"(item '{item.Description}'). Active rates: " +
                                string.Join(", ", activeRates.Select(r => $"{r.Rate}%")) + ".");
                        }

                        if (candidates.Count > 1)
                        {
                            return Error(
                                $"{candidates.Count} active VAT rates match {item.VatRatePercentage}% " +
                                $"(item '{item.Description}') — set vatRateId on the item to pick one. Candidates: " +
                                string.Join(", ", candidates.Select(DescribeVatRate)) + ".");
                        }

                        item.VatRateId = candidates[0].Id;
                    }
                }
            }

            var dto = new CreateInvoiceDto
            {
                DocumentType = parsedDocumentType,
                ClientId = clientId,
                IssuerId = issuer.Id,
                CurrencyId = resolvedCurrency.Id,
                IssueDate = parsedIssueDate,
                DueDate = parsedDueDate,
                VariableSymbol = variableSymbol,
                PaymentMethod = parsedPaymentMethod,
                Notes = notes,
                OriginalInvoiceId = originalInvoiceId,
                ApplyOss = applyOss,
                BankAccountId = bankAccountId,
                InvoiceItem = items
            };

            var result = await api.CreateInvoiceAsync(dto, ct);
            return JsonSerializer.Serialize(result, JsonOptions);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            return McpToolError.ToJson(ex);
        }
    }

    private static string Error(string message) =>
        JsonSerializer.Serialize(new { error = message }, JsonOptions);

    /// <summary>Renders one candidate in a "which VAT rate did you mean" error message.</summary>
    private static string DescribeVatRate(Fakvio.Contracts.Dto.VatRate.VatRateDto rate) =>
        $"id={rate.Id} name='{rate.Name}' validFrom={rate.ValidFrom:yyyy-MM-dd}" +
        (rate.ValidTo.HasValue ? $" validTo={rate.ValidTo.Value:yyyy-MM-dd}" : "");

    /// <summary>
    /// Issues (completes) a draft invoice.
    /// This generates the document number and transitions status from Draft → Completed.
    /// </summary>
    [McpServerTool(Title = "Complete invoice", ReadOnly = false, Destructive = true, Idempotent = false, OpenWorld = false), Description(
        "Complete (issue) a draft invoice. This generates the document number " +
        "and changes status from Draft to Completed. Cannot be undone.")]
    public static async Task<string> CompleteInvoice(
        IFakvioApiClient api,
        [Description("The invoice ID to complete")] long invoiceId,
        CancellationToken ct = default)
    {
        try
        {
            var result = await api.CompleteInvoiceAsync(invoiceId, ct);
            return JsonSerializer.Serialize(result, JsonOptions);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (TenantNotReadyApiException ex)
        {
            // Caught separately (#342) so the MCP client gets the structured payload — code,
            // missingFields, issues (each with its fix route) — instead of a flattened error
            // string that only the generic catch below could produce.
            //
            // Deliberately NOT routed through McpToolError.ToJson (#279): that sanitizes because
            // a raw API error body may carry internals. This payload is the opposite — our own
            // readiness contract (DEVGUIDE §4.12), already parsed into known fields, and it is
            // the whole point of the tool call to hand it to the client.
            return JsonSerializer.Serialize(new
            {
                error = ex.Message,
                code = TenantNotReadyApiException.ErrorCode,
                missingFields = ex.MissingFields,
                issues = ex.Issues
            }, JsonOptions);
        }
        catch (Exception ex)
        {
            return McpToolError.ToJson(ex);
        }
    }

    /// <summary>
    /// Marks a completed invoice as paid.
    /// Only works on invoices with status Completed.
    /// </summary>
    [McpServerTool(Title = "Mark invoice as paid", ReadOnly = false, Destructive = false, Idempotent = true, OpenWorld = false), Description(
        "Mark a completed invoice as paid. Only works on invoices " +
        "with status 'Completed'. Sets the PaidAt timestamp.")]
    public static async Task<string> MarkInvoicePaid(
        IFakvioApiClient api,
        [Description("The invoice ID to mark as paid")] long invoiceId,
        CancellationToken ct = default)
    {
        try
        {
            var result = await api.MarkInvoiceAsPaidAsync(invoiceId, ct);
            return JsonSerializer.Serialize(result, JsonOptions);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            return McpToolError.ToJson(ex);
        }
    }

    /// <summary>
    /// Sends an invoice via email with a PDF attachment.
    /// The API generates the PDF from the content template and attaches it.
    /// </summary>
    [McpServerTool(Title = "Send invoice by email", ReadOnly = false, Destructive = true, Idempotent = false, OpenWorld = true), Description(
        "Send an invoice via email with PDF attachment. " +
        "The system generates the PDF automatically and attaches it to the email.")]
    public static async Task<string> SendInvoiceEmail(
        IFakvioApiClient api,
        [Description("The invoice ID to send")] long invoiceId,
        [Description("Recipient email address")] string recipientEmail,
        [Description("Optional custom email subject")] string? subject = null,
        [Description("Optional custom message in the email body")] string? message = null,
        CancellationToken ct = default)
    {
        try
        {
            var dto = new SendInvoiceEmailDto
            {
                RecipientEmail = recipientEmail,
                Subject = subject,
                Message = message
            };

            await api.SendInvoiceEmailAsync(invoiceId, dto, ct);
            return JsonSerializer.Serialize(new { success = true, message = $"Invoice {invoiceId} sent to {recipientEmail}." }, JsonOptions);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            return McpToolError.ToJson(ex);
        }
    }

    /// <summary>
    /// Exports an invoice as a PDF file, returned as a base64-encoded string.
    /// The AI client can save this to a file or present it to the user.
    /// </summary>
    [McpServerTool(Title = "Export invoice as PDF", ReadOnly = true, Idempotent = true, OpenWorld = false), Description(
        "Export an invoice as PDF. Returns the PDF as a base64-encoded string. " +
        "Use this when the user asks to download, export, print, or get a PDF of an invoice. " +
        "You can find the invoice ID using FindInvoiceByNumber first.")]
    public static async Task<string> ExportInvoicePdf(
        IFakvioApiClient api,
        [Description("The invoice ID to export as PDF")] long invoiceId,
        CancellationToken ct = default)
    {
        try
        {
            // Fetch the invoice metadata for the file name
            var invoice = await api.GetInvoiceByIdAsync(invoiceId, ct);
            if (invoice is null)
                return JsonSerializer.Serialize(new { error = $"Invoice with ID {invoiceId} not found." }, JsonOptions);

            // Download the PDF bytes from the API
            var pdfBytes = await api.ExportInvoicePdfAsync(invoiceId, ct);

            // Build a descriptive file name based on document type
            var prefix = invoice.DocumentType == EDocumentType.CreditNote ? "CreditNote" : "Invoice";
            var fileName = $"{prefix}_{invoice.DocumentNumber ?? invoiceId.ToString()}.pdf";

            // Return base64-encoded PDF with metadata — AI client saves the file
            return JsonSerializer.Serialize(new
            {
                success = true,
                fileName,
                mimeType = "application/pdf",
                sizeBytes = pdfBytes.Length,
                base64Content = Convert.ToBase64String(pdfBytes)
            }, JsonOptions);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            return McpToolError.ToJson(ex);
        }
    }

    /// <summary>
    /// Exports an invoice as ISDOC 6.0.2 XML, returned as a base64-encoded string.
    /// ISDOC is the Czech electronic invoice standard importable by Pohoda, Money S3, Helios.
    /// The AI client can save this to a file with the .isdoc extension.
    /// </summary>
    [McpServerTool(Title = "Export invoice as ISDOC", ReadOnly = true, Idempotent = true, OpenWorld = false), Description(
        "Export an invoice as ISDOC 6.0.2 XML (Czech electronic invoice standard). " +
        "Returns the XML as a base64-encoded string. " +
        "Use this when the user asks to download or export an invoice as ISDOC for import into accounting software. " +
        "You can find the invoice ID using FindInvoiceByNumber first.")]
    public static async Task<string> ExportInvoiceIsdoc(
        IFakvioApiClient api,
        [Description("The invoice ID to export as ISDOC XML")] long invoiceId,
        CancellationToken ct = default)
    {
        try
        {
            // Fetch the invoice metadata for a meaningful file name
            var invoice = await api.GetInvoiceByIdAsync(invoiceId, ct);
            if (invoice is null)
                return JsonSerializer.Serialize(new { error = $"Invoice with ID {invoiceId} not found." }, JsonOptions);

            // Download the ISDOC XML bytes from the API
            var isdocBytes = await api.ExportInvoiceIsdocAsync(invoiceId, ct);

            // Build a descriptive file name based on document type
            var prefix = invoice.DocumentType == EDocumentType.CreditNote ? "CreditNote" : "Invoice";
            var fileName = $"{prefix}_{invoice.DocumentNumber ?? invoiceId.ToString()}.isdoc";

            // Return base64-encoded XML with metadata — AI client saves the file
            return JsonSerializer.Serialize(new
            {
                success = true,
                fileName,
                mimeType = "application/xml",
                sizeBytes = isdocBytes.Length,
                base64Content = Convert.ToBase64String(isdocBytes)
            }, JsonOptions);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            return McpToolError.ToJson(ex);
        }
    }

    /// <summary>
    /// Exports an invoice as UBL 2.1 / Peppol BIS Billing 3.0 XML, returned as a base64-encoded
    /// string (ADR 0002, F1.7). Aimed at SK e-invoicing from 2027 — the exported XML can be
    /// uploaded into a Peppol "digital courier" application. When the invoice is not ready for
    /// eInvoice export (still a Draft, a pro-forma, missing Peppol ID, …), the API answers 400
    /// and the error JSON carries the readable EINVOICE_* codes instead of a stack trace.
    /// </summary>
    [McpServerTool(Title = "Export invoice as UBL/Peppol eInvoice", ReadOnly = true, Idempotent = true, OpenWorld = false), Description(
        "Export an invoice as UBL 2.1 / Peppol BIS Billing 3.0 XML (SK e-invoicing 2027, Peppol network). " +
        "Returns the XML as a base64-encoded string. " +
        "Use this instead of ExportInvoiceIsdoc when the user asks for a Peppol / UBL / SK eFaktura export, " +
        "or wants a file to upload into a Peppol digital courier application. " +
        "You can find the invoice ID using FindInvoiceByNumber first.")]
    public static async Task<string> ExportInvoiceUbl(
        IFakvioApiClient api,
        [Description("The invoice ID to export as UBL/Peppol XML")] long invoiceId,
        CancellationToken ct = default)
    {
        try
        {
            // Fetch the invoice metadata for a meaningful file name
            var invoice = await api.GetInvoiceByIdAsync(invoiceId, ct);
            if (invoice is null)
                return JsonSerializer.Serialize(new { error = $"Invoice with ID {invoiceId} not found." }, JsonOptions);

            // Download the UBL XML bytes from the API
            var ublBytes = await api.ExportInvoiceUblAsync(invoiceId, ct);

            // Build a descriptive file name based on document type
            var prefix = invoice.DocumentType == EDocumentType.CreditNote ? "CreditNote" : "Invoice";
            var fileName = $"{prefix}_{invoice.DocumentNumber ?? invoiceId.ToString()}.xml";

            // Return base64-encoded XML with metadata — AI client saves the file
            return JsonSerializer.Serialize(new
            {
                success = true,
                fileName,
                mimeType = "application/xml",
                sizeBytes = ublBytes.Length,
                base64Content = Convert.ToBase64String(ublBytes)
            }, JsonOptions);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (TenantNotReadyApiException ex)
        {
            // Caught separately (#342, mirrors CompleteInvoice) so the MCP client gets the
            // structured EINVOICE_* payload — code, missingFields, issues (each with its fix
            // route) — instead of the flattened "internal_error" the generic catch below would
            // produce. This is the whole point of the tool call when export is refused.
            return JsonSerializer.Serialize(new
            {
                error = ex.Message,
                code = TenantNotReadyApiException.ErrorCode,
                missingFields = ex.MissingFields,
                issues = ex.Issues
            }, JsonOptions);
        }
        catch (Exception ex)
        {
            return McpToolError.ToJson(ex);
        }
    }

    /// <summary>
    /// Exports issued and/or received invoices for a date range as a single XML file formatted
    /// for a Czech accounting system (Pohoda / Money S3 / ABRA Flexi) — "Export do účetnictví".
    /// Unlike ExportInvoiceIsdoc/ExportInvoiceUbl this covers many invoices in one file, so it's
    /// the right tool when the user wants a whole month/period handed to their accountant.
    /// </summary>
    [McpServerTool(Title = "Export accounting period", ReadOnly = true, Idempotent = true, OpenWorld = false), Description(
        "Export issued and/or received invoices for a date range as one XML file for an accounting " +
        "system. Use this when the user asks to export invoices to Pohoda, Money S3 or ABRA Flexi, " +
        "or wants a batch/period handover to their accountant — not for a single invoice " +
        "(use ExportInvoiceIsdoc/ExportInvoiceUbl for that). Documents the target system cannot represent " +
        "(foreign currency for Money S3/ABRA Flexi, proformas for ABRA Flexi, advance-payment tax receipts, VAT rates other than 21/12/0 %) " +
        "are left out; skippedDocuments in the result says how many.")]
    public static async Task<string> ExportAccounting(
        IFakvioApiClient api,
        [Description("Target accounting system: 'Pohoda', 'MoneyS3' or 'AbraFlexi'")] string system,
        [Description("Start date (ISO 8601, e.g., '2026-01-01')")] string dateFrom,
        [Description("End date (ISO 8601, e.g., '2026-01-31')")] string dateTo,
        [Description("Include issued invoices/credit notes/proformas (default true)")] bool includeIssued = true,
        [Description("Include received (incoming) invoices (default true)")] bool includeReceived = true,
        CancellationToken ct = default)
    {
        try
        {
            if (!Enum.TryParse<EAccountingSystem>(system, ignoreCase: true, out var parsedSystem))
                return JsonSerializer.Serialize(new { error = $"Unknown accounting system '{system}'. Use 'Pohoda', 'MoneyS3' or 'AbraFlexi'." }, JsonOptions);

            if (!DateTime.TryParse(dateFrom, out var parsedFrom))
                return JsonSerializer.Serialize(new { error = $"Invalid dateFrom format: '{dateFrom}'. Use ISO 8601 (e.g., '2026-01-01')." }, JsonOptions);

            if (!DateTime.TryParse(dateTo, out var parsedTo))
                return JsonSerializer.Serialize(new { error = $"Invalid dateTo format: '{dateTo}'. Use ISO 8601 (e.g., '2026-01-31')." }, JsonOptions);

            var (xmlBytes, skipped) = await api.ExportAccountingAsync(parsedSystem, parsedFrom, parsedTo, includeIssued, includeReceived, ct);
            var fileName = $"{parsedSystem}_{parsedFrom:yyyyMMdd}-{parsedTo:yyyyMMdd}.xml";

            return JsonSerializer.Serialize(new
            {
                success = true,
                fileName,
                mimeType = "application/xml",
                sizeBytes = xmlBytes.Length,
                skippedDocuments = skipped,
                base64Content = Convert.ToBase64String(xmlBytes)
            }, JsonOptions);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            return McpToolError.ToJson(ex);
        }
    }

    /// <summary>
    /// Soft-deletes a draft invoice.
    /// Only invoices in Draft status can be deleted.
    /// </summary>
    [McpServerTool(Title = "Delete invoice", ReadOnly = false, Destructive = true, Idempotent = false, OpenWorld = false), Description(
        "Delete a draft invoice (soft delete). " +
        "Only invoices with status 'Draft' can be deleted.")]
    public static async Task<string> DeleteInvoice(
        IFakvioApiClient api,
        [Description("The invoice ID to delete")] long invoiceId,
        CancellationToken ct = default)
    {
        try
        {
            await api.DeleteInvoiceAsync(invoiceId, ct);
            return JsonSerializer.Serialize(new { success = true, message = $"Invoice {invoiceId} deleted." }, JsonOptions);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            return McpToolError.ToJson(ex);
        }
    }

    /// <summary>
    /// Changes the bank account shown on an existing invoice. Thin wrapper around
    /// PUT /api/invoice/{id} (UpdateInvoiceDto.BankAccountId) — reuses the same status guard as
    /// any other invoice update (Draft/Completed only, not Paid/Creditnoted).
    /// </summary>
    [McpServerTool(Title = "Set invoice bank account", ReadOnly = false, Destructive = false, Idempotent = true, OpenWorld = false), Description(
        "Change the bank account on an existing invoice to one of the issuer's accounts " +
        "(see the bankAccount list in get_issuer for ids). Only works on Draft or Completed " +
        "invoices — Paid and Creditnoted invoices cannot be changed.")]
    public static async Task<string> SetInvoiceBankAccount(
        IFakvioApiClient api,
        [Description("The invoice ID to update")] long invoiceId,
        [Description("The issuer's bank account ID to use (see get_issuer's bankAccount list)")] long bankAccountId,
        CancellationToken ct = default)
    {
        try
        {
            var dto = new UpdateInvoiceDto { BankAccountId = bankAccountId };
            var result = await api.UpdateInvoiceAsync(invoiceId, dto, ct);

            if (result is null)
                return Error($"Invoice with ID {invoiceId} not found.");

            return JsonSerializer.Serialize(result, JsonOptions);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            return McpToolError.ToJson(ex);
        }
    }
}

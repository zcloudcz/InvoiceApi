using System.ComponentModel;
using System.Text.Json;
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
    [McpServerTool, Description(
        "List invoices with pagination and filters. " +
        "Returns paginated results with invoice details including status, amounts, and client info.")]
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
        CancellationToken ct = default)
    {
        try
        {
            var filter = new InvoiceFilterDto
            {
                Page = page,
                PageSize = Math.Min(pageSize, 100),
                Search = search,
                IsOverdue = isOverdue
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
    [McpServerTool, Description(
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
    [McpServerTool, Description(
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
    /// Creates a new invoice or credit note from a JSON DTO.
    /// The invoice starts in Draft status and must be completed (issued) separately.
    /// </summary>
    [McpServerTool, Description(
        "Create a new invoice or credit note. The invoice is created in Draft status. " +
        "Requires a JSON object with: documentType ('Invoice'/'CreditNote'), clientId, issuerId, " +
        "currencyId, and invoiceItem array [{description, quantity, unitPrice, vatRatePercentage}]. " +
        "Optional: issueDate, dueDate, variableSymbol, bankAccountNumber, paymentMethod, notes.")]
    public static async Task<string> CreateInvoice(
        IFakvioApiClient api,
        [Description(
            "JSON string of CreateInvoiceDto. Example: " +
            "{\"documentType\":\"Invoice\",\"clientId\":1,\"issuerId\":2,\"currencyId\":1," +
            "\"invoiceItem\":[{\"description\":\"Web development\",\"quantity\":10,\"unit\":\"hrs\"," +
            "\"unitPrice\":1500,\"vatRatePercentage\":21}]}"
        )] string invoiceJson,
        CancellationToken ct = default)
    {
        // Parsing the model's own input is deliberately kept OUT of the try block
        // below — see McpToolError for why (issue #279).
        CreateInvoiceDto? dto;
        try
        {
            dto = JsonSerializer.Deserialize<CreateInvoiceDto>(invoiceJson, JsonOptions);
        }
        catch (JsonException ex)
        {
            return JsonSerializer.Serialize(new { error = $"Invalid JSON format: {ex.Message}" }, JsonOptions);
        }

        if (dto is null)
            return JsonSerializer.Serialize(new { error = "Invalid JSON: could not deserialize CreateInvoiceDto." }, JsonOptions);

        try
        {
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

    /// <summary>
    /// Issues (completes) a draft invoice.
    /// This generates the document number and transitions status from Draft → Completed.
    /// </summary>
    [McpServerTool, Description(
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
    [McpServerTool, Description(
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
    [McpServerTool, Description(
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
    [McpServerTool, Description(
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
    [McpServerTool, Description(
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
    /// Soft-deletes a draft invoice.
    /// Only invoices in Draft status can be deleted.
    /// </summary>
    [McpServerTool, Description(
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
}

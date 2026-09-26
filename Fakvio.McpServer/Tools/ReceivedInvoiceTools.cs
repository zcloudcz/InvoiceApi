using System.ComponentModel;
using System.Text.Json;
using Fakvio.Contracts.Dto.ReceivedInvoice;
using Fakvio.Domain.Enums;
using Fakvio.McpServer.Client;
using Fakvio.McpServer.Configuration;
using ModelContextProtocol.Server;

namespace Fakvio.McpServer.Tools;

/// <summary>
/// MCP tools for managing received (incoming) invoices — expenses from suppliers.
/// Provides CRUD operations and status transitions (Received -> Approved -> Paid).
///
/// Junior note: These tools mirror the ReceivedInvoiceController endpoints.
/// Each tool calls IFakvioApiClient methods and returns JSON results.
/// </summary>
[McpServerToolType]
public static class ReceivedInvoiceTools
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        WriteIndented = true,
        Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter() }
    };

    /// <summary>
    /// Lists received invoices with pagination and filtering.
    /// </summary>
    [McpServerTool(Title = "List received invoices", ReadOnly = true, Idempotent = true, OpenWorld = false), Description(
        "List received (incoming) invoices with pagination and filters. " +
        "These are expenses from suppliers. " +
        "Filter by status ('Received', 'Approved', 'Paid', 'Rejected'), supplier ID, date range, etc.")]
    public static async Task<string> ListReceivedInvoices(
        IFakvioApiClient api,
        [Description("Page number (1-based, default 1)")] int page = 1,
        [Description("Items per page (default 50, max 100)")] int pageSize = 50,
        [Description("Search in document number, supplier name, variable symbol")] string? search = null,
        [Description("Filter by status: 'Received', 'Approved', 'Paid', 'Rejected'")] string? status = null,
        [Description("Filter by supplier (client) ID")] long? supplierId = null,
        [Description("Filter from issue date (ISO 8601)")] string? issueDateFrom = null,
        [Description("Filter to issue date (ISO 8601)")] string? issueDateTo = null,
        [Description("Show only overdue (unpaid past due date)")] bool? isOverdue = null,
        CancellationToken ct = default)
    {
        try
        {
            var filter = new ReceivedInvoiceFilterDto
            {
                Page = page,
                PageSize = Math.Min(pageSize, 100),
                Search = search,
                SupplierId = supplierId,
                IsOverdue = isOverdue,
                SortBy = "ReceivedDate",
                SortDirection = "desc"
            };

            if (!string.IsNullOrEmpty(status) &&
                Enum.TryParse<EReceivedInvoiceStatus>(status, ignoreCase: true, out var st))
                filter.Status = st;

            if (!string.IsNullOrEmpty(issueDateFrom) && DateTime.TryParse(issueDateFrom, out var from))
                filter.IssueDateFrom = from;

            if (!string.IsNullOrEmpty(issueDateTo) && DateTime.TryParse(issueDateTo, out var to))
                filter.IssueDateTo = to;

            var result = await api.GetReceivedInvoicesPagedAsync(filter, ct);
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
    /// Gets a single received invoice by ID.
    /// </summary>
    [McpServerTool(Title = "Get received invoice", ReadOnly = true, Idempotent = true, OpenWorld = false), Description(
        "Get a received (incoming) invoice by ID. Returns full details including line items and supplier info.")]
    public static async Task<string> GetReceivedInvoice(
        IFakvioApiClient api,
        [Description("The received invoice ID")] long id,
        CancellationToken ct = default)
    {
        try
        {
            var result = await api.GetReceivedInvoiceByIdAsync(id, ct);
            if (result is null)
                return JsonSerializer.Serialize(new { error = $"Received invoice with ID {id} not found." }, JsonOptions);

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
    /// Creates a new received invoice.
    /// </summary>
    [McpServerTool(Title = "Create received invoice", ReadOnly = false, Destructive = false, Idempotent = false, OpenWorld = false), Description(
        "Create a new received (incoming) invoice. Pass JSON with: " +
        "supplierId (required), currencyId (required), items[] (required, at least 1), " +
        "documentNumber, issueDate, receivedDate, dueDate, taxableSupplyDate, " +
        "variableSymbol, paymentMethod, bankAccountNumber, iban, swift, notes. " +
        "paymentMethod must be one of: BankTransfer, Cash, CreditCard, PayPal, Other (anything else, e.g. 'Apple Pay', goes to notes). " +
        "Each item needs: description, quantity, unitPrice, vatRatePercentage (or vatRateId); optional productCode, notes. " +
        "Negative unitPrice is allowed for discount lines. " +
        "To attach the source PDF afterwards, call upload_received_invoice_attachment with the returned id.")]
    public static async Task<string> CreateReceivedInvoice(
        IFakvioApiClient api,
        [Description("JSON string of CreateReceivedInvoiceDto")] string invoiceJson,
        CancellationToken ct = default)
    {
        // Parsing the model's own input is deliberately kept OUT of the try block
        // below — see McpToolError for why (issue #279).
        CreateReceivedInvoiceDto? dto;
        try
        {
            dto = JsonSerializer.Deserialize<CreateReceivedInvoiceDto>(invoiceJson, JsonOptions);
        }
        catch (JsonException ex)
        {
            return JsonSerializer.Serialize(new { error = $"Invalid JSON format: {ex.Message}" }, JsonOptions);
        }

        if (dto is null)
            return JsonSerializer.Serialize(new { error = "Failed to parse invoice JSON." }, JsonOptions);

        try
        {
            var result = await api.CreateReceivedInvoiceAsync(dto, ct);
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
    /// Approves a received invoice for payment.
    /// </summary>
    [McpServerTool(Title = "Approve received invoice", ReadOnly = false, Destructive = false, Idempotent = true, OpenWorld = false), Description(
        "Approve a received invoice for payment. Transitions from 'Received' to 'Approved'. " +
        "Only invoices in 'Received' status can be approved.")]
    public static async Task<string> ApproveReceivedInvoice(
        IFakvioApiClient api,
        [Description("The received invoice ID to approve")] long id,
        CancellationToken ct = default)
    {
        try
        {
            var result = await api.ApproveReceivedInvoiceAsync(id, ct);
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
    /// Marks a received invoice as paid.
    /// </summary>
    [McpServerTool(Title = "Mark received invoice as paid", ReadOnly = false, Destructive = false, Idempotent = true, OpenWorld = false), Description(
        "Mark a received invoice as paid. Transitions from 'Approved' to 'Paid'. " +
        "Only invoices in 'Approved' status can be marked as paid.")]
    public static async Task<string> MarkReceivedInvoicePaid(
        IFakvioApiClient api,
        [Description("The received invoice ID to mark as paid")] long id,
        CancellationToken ct = default)
    {
        try
        {
            var result = await api.MarkReceivedInvoicePaidAsync(id, ct);
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
    /// Deletes a received invoice (soft delete).
    /// </summary>
    [McpServerTool(Title = "Delete received invoice", ReadOnly = false, Destructive = true, Idempotent = false, OpenWorld = false), Description(
        "Delete a received invoice (soft delete). Only 'Received' or 'Rejected' invoices can be deleted.")]
    public static async Task<string> DeleteReceivedInvoice(
        IFakvioApiClient api,
        [Description("The received invoice ID to delete")] long id,
        CancellationToken ct = default)
    {
        try
        {
            await api.DeleteReceivedInvoiceAsync(id, ct);
            return JsonSerializer.Serialize(new { success = true, message = $"Received invoice {id} deleted." }, JsonOptions);
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
    /// Maximum attachment size accepted from a URL — mirrors FileAttachmentController.MaxFileSizeBytes,
    /// so a too-big download is stopped here instead of after it was fully transferred.
    /// </summary>
    private const long MaxAttachmentBytes = 50 * 1024 * 1024;

    /// <summary>
    /// Uploads a file (typically the supplier's PDF) as an attachment of a received invoice.
    /// </summary>
    [McpServerTool(Title = "Upload received invoice attachment", ReadOnly = false, Destructive = false, Idempotent = false, OpenWorld = true), Description(
        "Attach a file (e.g. the supplier's original PDF) to an existing received invoice. " +
        "Give the file in exactly ONE way: fileUrl (https link the server downloads — preferred, works for any size), " +
        "filePath (absolute path on the machine running the MCP server — only when the server runs locally over stdio), " +
        "or base64Content (inline, small files only). Max 50 MB.")]
    public static async Task<string> UploadReceivedInvoiceAttachment(
        IFakvioApiClient api,
        McpServerSettings settings,
        IHttpClientFactory httpClientFactory,
        [Description("The received invoice ID")] long id,
        [Description("https URL of the file; the server downloads it")] string? fileUrl = null,
        [Description("Absolute local path of the file (stdio/local server only)")] string? filePath = null,
        [Description("File content encoded as base64 (small files only)")] string? base64Content = null,
        [Description("File name including extension; defaults to the name from URL/path, or 'attachment.pdf'")] string? fileName = null,
        [Description("MIME type, default 'application/pdf'")] string contentType = "application/pdf",
        [Description("Optional description (max 500 chars)")] string? description = null,
        CancellationToken ct = default)
    {
        var sources = new[] { fileUrl, filePath, base64Content }.Count(v => !string.IsNullOrWhiteSpace(v));
        if (sources != 1)
            return Error("Provide exactly one of fileUrl, filePath or base64Content.");

        // Obtaining the bytes is the model's-input side and reports precise errors;
        // the API call below goes through the sanitized catch-all (McpToolError).
        byte[] content;
        try
        {
            if (!string.IsNullOrWhiteSpace(fileUrl))
            {
                if (!Uri.TryCreate(fileUrl, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps)
                    return Error("fileUrl must be an absolute https URL.");
                // SSRF guard: the download runs from the server's network, so IP literals and
                // loopback could reach internal services that the caller cannot.
                if (uri.IsLoopback || uri.HostNameType is UriHostNameType.IPv4 or UriHostNameType.IPv6)
                    return Error("fileUrl must point to a public host name, not an IP address or localhost.");

                fileName ??= Path.GetFileName(uri.LocalPath);
                content = await DownloadAsync(httpClientFactory.CreateClient(), uri, ct);
            }
            else if (!string.IsNullOrWhiteSpace(filePath))
            {
                if (!settings.AllowLocalFiles)
                    return Error("filePath is only available when the MCP server runs locally (stdio). Use fileUrl instead.");
                if (!Path.IsPathRooted(filePath))
                    return Error("filePath must be an absolute path.");
                if (!File.Exists(filePath))
                    return Error($"File not found: {filePath}");

                fileName ??= Path.GetFileName(filePath);
                content = await File.ReadAllBytesAsync(filePath, ct);
            }
            else
            {
                content = Convert.FromBase64String(base64Content!);
            }
        }
        catch (FormatException)
        {
            return Error("base64Content is not valid base64.");
        }
        catch (HttpRequestException ex)
        {
            return Error($"Download failed: {ex.Message}");
        }
        catch (InvalidOperationException ex)
        {
            return Error(ex.Message);
        }

        if (content.Length == 0)
            return Error("File content is empty.");
        if (content.Length > MaxAttachmentBytes)
            return Error("File exceeds the 50 MB limit.");
        if (string.IsNullOrWhiteSpace(fileName))
            fileName = "attachment.pdf";

        try
        {
            var result = await api.UploadFileAttachmentAsync("ReceivedInvoice", id, fileName, contentType, content, description, ct);
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
    /// Downloads a URL with the size cap enforced while streaming — Content-Length can be
    /// missing or lie, so the header check alone is not enough.
    /// </summary>
    private static async Task<byte[]> DownloadAsync(HttpClient http, Uri uri, CancellationToken ct)
    {
        using var response = await http.GetAsync(uri, HttpCompletionOption.ResponseHeadersRead, ct);
        response.EnsureSuccessStatusCode();

        if (response.Content.Headers.ContentLength > MaxAttachmentBytes)
            throw new InvalidOperationException("File exceeds the 50 MB limit.");

        await using var stream = await response.Content.ReadAsStreamAsync(ct);
        using var buffer = new MemoryStream();
        var chunk = new byte[81920];
        int read;
        while ((read = await stream.ReadAsync(chunk, ct)) > 0)
        {
            buffer.Write(chunk, 0, read);
            if (buffer.Length > MaxAttachmentBytes)
                throw new InvalidOperationException("File exceeds the 50 MB limit.");
        }
        return buffer.ToArray();
    }

    private static string Error(string message) =>
        JsonSerializer.Serialize(new { error = message }, JsonOptions);
}

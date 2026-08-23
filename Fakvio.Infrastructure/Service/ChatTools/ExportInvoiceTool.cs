using Fakvio.Application.Service;
using Fakvio.Contracts.Dto.Chat;
using Fakvio.Domain.Enums;
using Microsoft.Extensions.Logging;

namespace Fakvio.Infrastructure.Service.ChatTools;

/// <summary>
/// Chat tool that exports (downloads) an invoice or credit note as a PDF or ISDOC file.
/// When a user says "Export invoice FV-2024-0001" or "Download PDF for client ABC",
/// this tool:
/// 1. Finds the invoice by document number or by client name (most recent)
/// 2. Returns a ChatUiAction of type "download" with the export endpoint URL
/// 3. The Blazor client fetches the bytes and triggers a browser download via JS interop
///
/// Junior note: This tool does NOT generate the file itself — it only resolves the invoice
/// and returns the API endpoint URL. The actual PDF / ISDOC generation happens when the Blazor
/// client calls that endpoint. This keeps the tool lightweight and fast.
/// </summary>
public class ExportInvoiceTool : IChatTool
{
    private readonly IInvoiceService _invoiceService;
    private readonly IClientService _clientService;
    private readonly ILogger<ExportInvoiceTool> _logger;

    public ExportInvoiceTool(
        IInvoiceService invoiceService,
        IClientService clientService,
        ILogger<ExportInvoiceTool> logger)
    {
        _invoiceService = invoiceService;
        _clientService = clientService;
        _logger = logger;
    }

    public string ToolName => "export_invoice";

    public string Description =>
        "Export/download an invoice or credit note as a PDF or ISDOC file (ISDOC is the Czech " +
        "electronic invoice standard imported by Pohoda, Money S3 and Helios). " +
        "Finds the document by number, by client name (most recent), or exports the most recent invoice if no parameters given.";

    /// <summary>Export formats, spelled the way the model must send them.</summary>
    private const string PdfFormat = "pdf";
    private const string IsdocFormat = "isdoc";

    /// <summary>
    /// Parameter schema — static because it never changes per instance.
    /// All parameters are optional on purpose: with none, the most recent invoice is exported as PDF.
    /// </summary>
    private static readonly ChatToolParameter[] Schema =
    [
        new()
        {
            Name = "document_number",
            Type = ChatToolParameterType.String,
            Description = "Invoice number as printed on the document (e.g. FV-2024-0001)"
        },
        new()
        {
            Name = "client_name",
            Type = ChatToolParameterType.String,
            Description = "Client name — exports the most recent invoice for this client"
        },
        new()
        {
            Name = "format",
            Type = ChatToolParameterType.String,
            Description = "File format to download (default pdf). Use isdoc for import into accounting software.",
            AllowedValues = [PdfFormat, IsdocFormat]
        }
    ];

    public IReadOnlyList<ChatToolParameter> Parameters => Schema;

    /// <summary>
    /// Finds the invoice and returns a download action with the PDF endpoint URL.
    /// Search priority: document_number first, then client_name (most recent invoice).
    /// </summary>
    public async Task<ChatToolResult> ExecuteAsync(
        Dictionary<string, string> parameters,
        CancellationToken ct = default)
    {
        parameters.TryGetValue("document_number", out var documentNumber);
        parameters.TryGetValue("client_name", out var clientName);

        // The executor validated the value against AllowedValues but dispatches it raw, so it
        // is trimmed and lower-cased here; anything absent means the usual PDF (#268).
        var format = parameters.TryGetValue("format", out var rawFormat)
                     && rawFormat.Trim().Equals(IsdocFormat, StringComparison.OrdinalIgnoreCase)
            ? IsdocFormat
            : PdfFormat;

        _logger.LogInformation(
            "ExportInvoiceTool: document_number={DocNum}, client_name={Client}, format={Format}",
            documentNumber, clientName, format);

        // --- Path 1: Find by document number ---
        if (!string.IsNullOrWhiteSpace(documentNumber))
        {
            return await ExportByDocumentNumberAsync(documentNumber.Trim(), format, ct);
        }

        // --- Path 2: Find by client name (most recent invoice) ---
        if (!string.IsNullOrWhiteSpace(clientName))
        {
            return await ExportByClientNameAsync(clientName.Trim(), format, ct);
        }

        // --- Path 3: No parameters — export the most recent invoice ---
        return await ExportMostRecentAsync(format, ct);
    }

    /// <summary>
    /// Finds an invoice by its document number and returns a download action.
    /// </summary>
    private async Task<ChatToolResult> ExportByDocumentNumberAsync(
        string documentNumber, string format, CancellationToken ct)
    {
        var invoice = await _invoiceService.GetInvoiceByDocumentNumberAsync(documentNumber, ct);
        if (invoice == null)
        {
            return ChatToolResult.Failure(
                $"Invoice with document number '{documentNumber}' not found.");
        }

        return BuildDownloadResult(invoice.Id, invoice.DocumentNumber ?? documentNumber,
            invoice.DocumentType, invoice.ClientName ?? "", format);
    }

    /// <summary>
    /// Exports the most recent invoice in the system (no filter).
    /// </summary>
    private async Task<ChatToolResult> ExportMostRecentAsync(string format, CancellationToken ct)
    {
        var invoices = await _invoiceService.GetAllInvoicesAsync(null, null, null, null, ct);
        var latest = invoices
            .OrderByDescending(i => i.CreatedAt)
            .FirstOrDefault();

        if (latest == null)
        {
            return ChatToolResult.Failure("No invoices found in the system.");
        }

        return BuildDownloadResult(latest.Id,
            latest.DocumentNumber ?? latest.Id.ToString(),
            latest.DocumentType,
            latest.ClientName ?? "",
            format);
    }

    /// <summary>
    /// Finds the most recent invoice for a client and returns a download action.
    /// Searches clients by name (case-insensitive substring match), then gets their latest invoice.
    /// </summary>
    private async Task<ChatToolResult> ExportByClientNameAsync(
        string clientName, string format, CancellationToken ct)
    {
        // Search for clients matching the name
        var clients = await _clientService.GetAllClientsAsync(false, ct);
        var matches = clients
            .Where(c => c.CompanyName != null &&
                        c.CompanyName.Contains(clientName, StringComparison.OrdinalIgnoreCase))
            .ToList();

        if (matches.Count == 0)
        {
            return ChatToolResult.Failure(
                $"No client found matching '{clientName}'.");
        }

        if (matches.Count > 5)
        {
            return ChatToolResult.Failure(
                $"Too many clients match '{clientName}' ({matches.Count}). Please be more specific.");
        }

        // Get all invoices and find the most recent one for matching clients
        var clientIds = matches.Select(c => c.Id).ToHashSet();
        var invoices = await _invoiceService.GetAllInvoicesAsync(
            null, null, null, null, ct);
        var latestInvoice = invoices
            .Where(i => i.ClientId.HasValue && clientIds.Contains(i.ClientId.Value))
            .OrderByDescending(i => i.CreatedAt)
            .FirstOrDefault();

        if (latestInvoice == null)
        {
            var clientNames = string.Join(", ", matches.Select(c => c.CompanyName));
            return ChatToolResult.Failure(
                $"No invoices found for client(s): {clientNames}.");
        }

        return BuildDownloadResult(latestInvoice.Id,
            latestInvoice.DocumentNumber ?? latestInvoice.Id.ToString(),
            latestInvoice.DocumentType,
            latestInvoice.ClientName ?? clientName,
            format);
    }

    /// <summary>
    /// Builds a ChatToolResult with a download action pointing to the export endpoint of the
    /// requested format. Uses localized-friendly file naming (Invoice/CreditNote prefix).
    ///
    /// Both endpoints share the same shape (<c>/api/invoice/{id}/{format}</c>) and the file
    /// extension equals the format, so one line covers PDF and ISDOC.
    /// </summary>
    private static ChatToolResult BuildDownloadResult(
        long invoiceId, string documentNumber, EDocumentType documentType, string clientName, string format)
    {
        // Build the API endpoint URL for the download
        var downloadUrl = $"/api/invoice/{invoiceId}/{format}";

        // Build a descriptive file name
        var typePrefix = documentType == EDocumentType.CreditNote ? "CreditNote" : "Invoice";
        var fileName = $"{typePrefix}_{documentNumber}.{format}";

        var description = documentType == EDocumentType.CreditNote ? "credit note" : "invoice";
        var outputText = $"Exporting {description} {documentNumber}" +
                         (string.IsNullOrEmpty(clientName) ? "" : $" for {clientName}") +
                         $" as {format.ToUpperInvariant()}.";

        // The MIME type must match the format — the client hands it to the browser, and an
        // .isdoc announced as application/pdf is a PDF reader waiting to fail on XML.
        var mimeType = format == IsdocFormat ? "application/xml" : "application/pdf";

        return ChatToolResult.SuccessWithAction(
            outputText,
            ChatUiAction.Download(downloadUrl, fileName, mimeType));
    }
}

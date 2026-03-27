using Fakvio.Application.Service;
using Fakvio.Contracts.Dto.Chat;
using Fakvio.Domain.Enums;
using Microsoft.Extensions.Logging;

namespace Fakvio.Infrastructure.Service.ChatTools;

/// <summary>
/// Chat tool that exports (downloads) an invoice or credit note as a PDF file.
/// When a user says "Export invoice FV-2024-0001" or "Download PDF for client ABC",
/// this tool:
/// 1. Finds the invoice by document number or by client name (most recent)
/// 2. Returns a ChatUiAction of type "download" with the PDF endpoint URL
/// 3. The Blazor client fetches the PDF bytes and triggers a browser download via JS interop
///
/// Junior note: This tool does NOT generate the PDF itself — it only resolves the invoice
/// and returns the API endpoint URL. The actual PDF generation happens when the Blazor
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
        "Export/download an invoice or credit note as a PDF file. " +
        "Finds the document by number, by client name (most recent), or exports the most recent invoice if no parameters given.";

    public string ParameterDescription =>
        "document_number (string, optional): Invoice number (e.g., FV-2024-0001). " +
        "client_name (string, optional): Client name — exports the most recent invoice for this client. " +
        "If neither is provided, exports the most recent invoice in the system.";

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

        _logger.LogInformation(
            "ExportInvoiceTool: document_number={DocNum}, client_name={Client}",
            documentNumber, clientName);

        // --- Path 1: Find by document number ---
        if (!string.IsNullOrWhiteSpace(documentNumber))
        {
            return await ExportByDocumentNumberAsync(documentNumber.Trim(), ct);
        }

        // --- Path 2: Find by client name (most recent invoice) ---
        if (!string.IsNullOrWhiteSpace(clientName))
        {
            return await ExportByClientNameAsync(clientName.Trim(), ct);
        }

        // --- Path 3: No parameters — export the most recent invoice ---
        return await ExportMostRecentAsync(ct);
    }

    /// <summary>
    /// Finds an invoice by its document number and returns a download action.
    /// </summary>
    private async Task<ChatToolResult> ExportByDocumentNumberAsync(
        string documentNumber, CancellationToken ct)
    {
        var invoice = await _invoiceService.GetInvoiceByDocumentNumberAsync(documentNumber, ct);
        if (invoice == null)
        {
            return ChatToolResult.Failure(
                $"Invoice with document number '{documentNumber}' not found.");
        }

        return BuildDownloadResult(invoice.Id, invoice.DocumentNumber ?? documentNumber,
            invoice.DocumentType, invoice.ClientName ?? "");
    }

    /// <summary>
    /// Exports the most recent invoice in the system (no filter).
    /// </summary>
    private async Task<ChatToolResult> ExportMostRecentAsync(CancellationToken ct)
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
            latest.ClientName ?? "");
    }

    /// <summary>
    /// Finds the most recent invoice for a client and returns a download action.
    /// Searches clients by name (case-insensitive substring match), then gets their latest invoice.
    /// </summary>
    private async Task<ChatToolResult> ExportByClientNameAsync(
        string clientName, CancellationToken ct)
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
            latestInvoice.ClientName ?? clientName);
    }

    /// <summary>
    /// Builds a ChatToolResult with a download action pointing to the invoice PDF endpoint.
    /// Uses localized-friendly file naming (Invoice/CreditNote prefix).
    /// </summary>
    private static ChatToolResult BuildDownloadResult(
        long invoiceId, string documentNumber, EDocumentType documentType, string clientName)
    {
        // Build the API endpoint URL for PDF download
        var pdfUrl = $"/api/invoice/{invoiceId}/pdf";

        // Build a descriptive file name
        var typePrefix = documentType == EDocumentType.CreditNote ? "CreditNote" : "Invoice";
        var fileName = $"{typePrefix}_{documentNumber}.pdf";

        var description = documentType == EDocumentType.CreditNote ? "credit note" : "invoice";
        var outputText = $"Exporting {description} {documentNumber}" +
                         (string.IsNullOrEmpty(clientName) ? "" : $" for {clientName}") +
                         " as PDF.";

        return ChatToolResult.SuccessWithAction(
            outputText,
            ChatUiAction.Download(pdfUrl, fileName));
    }
}

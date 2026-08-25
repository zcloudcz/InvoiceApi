using Fakvio.Application.Service;
using Fakvio.Contracts.Dto.Invoice;
using Fakvio.Domain.Enums;
using Microsoft.Extensions.Logging;

namespace Fakvio.Infrastructure.Service.ChatTools;

/// <summary>
/// Chat tool that deletes a DRAFT issued invoice (soft delete — it can be restored on the
/// Invoices page). Mirrors MCP <c>DeleteInvoice</c> and the Delete action in the invoice grid,
/// which is offered for drafts only.
///
/// Destructive, so it goes through the confirm gate (<see cref="IConfirmableChatTool"/>,
/// DEVGUIDE §4.7). Note that the gate is a UX flow, NOT an authorization boundary: a model can
/// send <c>confirm: true</c> on the first call. Deleting a draft is therefore deliberately the
/// only deletion offered here — it is exactly what the user can already do with one click in
/// the grid, and it is reversible. Issued documents keep their numbering intact.
/// </summary>
public class DeleteInvoiceTool : IConfirmableChatTool
{
    private readonly IInvoiceService _invoiceService;
    private readonly ILogger<DeleteInvoiceTool> _logger;

    public DeleteInvoiceTool(IInvoiceService invoiceService, ILogger<DeleteInvoiceTool> logger)
    {
        _invoiceService = invoiceService;
        _logger = logger;
    }

    public string ToolName => "delete_invoice";

    public string Description =>
        "Delete a DRAFT issued invoice (soft delete — it can be restored on the Invoices page). " +
        "Issued documents cannot be deleted from chat. Identify the draft by ID or document number.";

    public IReadOnlyList<ChatToolParameter> Parameters => InvoiceLookup.IdentitySchema;

    public async Task<ChatToolResult> BuildPreviewAsync(
        Dictionary<string, string> parameters,
        CancellationToken ct = default)
    {
        var (invoice, error) = await LoadDeletableAsync(parameters, ct);
        if (invoice is null)
            return ChatToolResult.Failure(error!);

        return ChatToolResult.Success(
            $"Will delete draft {InvoiceLookup.Describe(invoice)}. " +
            "The deletion is a soft delete — the draft can be restored on the Invoices page.");
    }

    public async Task<ChatToolResult> ExecuteAsync(
        Dictionary<string, string> parameters,
        CancellationToken ct = default)
    {
        // Re-checked: the preview and this call are not paired (see IConfirmableChatTool).
        var (invoice, error) = await LoadDeletableAsync(parameters, ct);
        if (invoice is null)
            return ChatToolResult.Failure(error!);

        _logger.LogInformation("DeleteInvoiceTool deleting draft invoice {InvoiceId}", invoice.Id);

        var deleted = await _invoiceService.DeleteInvoiceAsync(invoice.Id, ct);

        return deleted
            ? ChatToolResult.Success($"Deleted draft {InvoiceLookup.Describe(invoice)}.")
            : ChatToolResult.Failure($"Issued invoice with ID {invoice.Id} could not be deleted.");
    }

    /// <summary>
    /// Resolves the document and refuses anything that is not a draft. Deliberately stricter
    /// than <c>InvoiceService.DeleteInvoiceAsync</c> (which also allows the last Completed
    /// document): the assistant should not be the place where a numbered tax document
    /// disappears.
    /// </summary>
    private async Task<(InvoiceDto? Invoice, string? Error)> LoadDeletableAsync(
        Dictionary<string, string> parameters, CancellationToken ct)
    {
        var (invoice, error) = await InvoiceLookup.ResolveAsync(_invoiceService, parameters, ct);
        if (invoice is null)
            return (null, error);

        return invoice.Status == EInvoiceStatus.Draft
            ? (invoice, null)
            : (null, $"{InvoiceLookup.Describe(invoice)} is {invoice.Status} — only a Draft can be deleted from chat. " +
                     "Delete an issued document on the Invoices page.");
    }
}

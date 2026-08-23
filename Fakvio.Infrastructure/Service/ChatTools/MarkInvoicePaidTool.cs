using Fakvio.Application.Service;
using Fakvio.Contracts.Dto.Invoice;
using Fakvio.Domain.Enums;
using Microsoft.Extensions.Logging;

namespace Fakvio.Infrastructure.Service.ChatTools;

/// <summary>
/// Chat tool that marks a completed issued invoice as paid. Mirrors MCP <c>MarkInvoicePaid</c>.
///
/// Data-changing, so it goes through the confirm gate (<see cref="IConfirmableChatTool"/>,
/// DEVGUIDE §4.7): the first call only describes what would be marked.
///
/// The payment date is always "now" — the same thing the Mark paid button in the invoice grid
/// does. Back-dating a payment is a bookkeeping decision that belongs to the payment matching
/// screen, not to a chat shortcut.
/// </summary>
public class MarkInvoicePaidTool : IConfirmableChatTool
{
    private readonly IInvoiceService _invoiceService;
    private readonly ILogger<MarkInvoicePaidTool> _logger;

    public MarkInvoicePaidTool(IInvoiceService invoiceService, ILogger<MarkInvoicePaidTool> logger)
    {
        _invoiceService = invoiceService;
        _logger = logger;
    }

    public string ToolName => "mark_invoice_paid";

    public string Description =>
        "Mark a COMPLETED issued invoice as paid (status Completed → Paid, payment date = now). " +
        "Identify the invoice by ID or document number.";

    public IReadOnlyList<ChatToolParameter> Parameters => InvoiceLookup.IdentitySchema;

    public async Task<ChatToolResult> BuildPreviewAsync(
        Dictionary<string, string> parameters,
        CancellationToken ct = default)
    {
        var (invoice, error) = await LoadPayableAsync(parameters, ct);
        if (invoice is null)
            return ChatToolResult.Failure(error!);

        return ChatToolResult.Success(
            $"Will mark {InvoiceLookup.Describe(invoice)} as paid: status Completed → Paid, " +
            "payment date set to now.");
    }

    public async Task<ChatToolResult> ExecuteAsync(
        Dictionary<string, string> parameters,
        CancellationToken ct = default)
    {
        // Re-checked: the preview and this call are not paired (see IConfirmableChatTool).
        var (invoice, error) = await LoadPayableAsync(parameters, ct);
        if (invoice is null)
            return ChatToolResult.Failure(error!);

        _logger.LogInformation("MarkInvoicePaidTool marking invoice {InvoiceId} as paid", invoice.Id);

        var paid = await _invoiceService.MarkAsPaidAsync(invoice.Id, paidAt: null, ct);
        if (paid is null)
            return ChatToolResult.Failure($"Issued invoice with ID {invoice.Id} not found.");

        return ChatToolResult.Success(
            $"{paid.DocumentType} {paid.DocumentNumber ?? $"(ID {paid.Id})"} for {paid.ClientName} " +
            $"is now {paid.Status}, paid at {ChatToolDates.Format(paid.PaidAt)}.");
    }

    /// <summary>
    /// Resolves the document and refuses anything that is not Completed — the same rule
    /// <c>InvoiceService.MarkAsPaidAsync</c> enforces, checked here so the user gets a readable
    /// reason before confirming instead of an exception afterwards.
    /// </summary>
    private async Task<(InvoiceDto? Invoice, string? Error)> LoadPayableAsync(
        Dictionary<string, string> parameters, CancellationToken ct)
    {
        var (invoice, error) = await InvoiceLookup.ResolveAsync(_invoiceService, parameters, ct);
        if (invoice is null)
            return (null, error);

        return invoice.Status == EInvoiceStatus.Completed
            ? (invoice, null)
            : (null, $"{InvoiceLookup.Describe(invoice)} is {invoice.Status} — only a Completed invoice can be marked as paid.");
    }
}

using Fakvio.Application.Service;
using Fakvio.Contracts.Dto.Invoice;
using Fakvio.Domain.Enums;
using Microsoft.Extensions.Logging;

namespace Fakvio.Infrastructure.Service.ChatTools;

/// <summary>
/// Chat tool that switches an issued (Completed) invoice back to Draft so update_invoice can edit it.
/// Mirrors MCP <c>revert_invoice_to_draft</c>.
///
/// Data-changing, so it goes through the confirm gate (<see cref="IConfirmableChatTool"/>,
/// DEVGUIDE §4.7): the first call only describes the consequences, the model must ask the user.
/// </summary>
public class RevertInvoiceToDraftTool : IConfirmableChatTool
{
    private readonly IInvoiceService _invoiceService;
    private readonly ILogger<RevertInvoiceToDraftTool> _logger;

    public RevertInvoiceToDraftTool(IInvoiceService invoiceService, ILogger<RevertInvoiceToDraftTool> logger)
    {
        _invoiceService = invoiceService;
        _logger = logger;
    }

    public string ToolName => "revert_invoice_to_draft";

    public string Description =>
        "Switch an issued COMPLETED invoice back to Draft so it can be edited with update_invoice. " +
        "Call it only after the user explicitly agreed: the document becomes a draft again, must be issued with " +
        "complete_invoice after editing and re-sent if it was already e-mailed (the invoice still shows as e-mailed; " +
        "re-completing fires the invoice webhook again). The document number is kept. " +
        "Identify the invoice by ID or document number.";

    public IReadOnlyList<ChatToolParameter> Parameters => InvoiceLookup.IdentitySchema;

    public async Task<ChatToolResult> BuildPreviewAsync(
        Dictionary<string, string> parameters,
        CancellationToken ct = default)
    {
        var (invoice, error) = await LoadRevertibleAsync(parameters, ct);
        if (invoice is null)
            return ChatToolResult.Failure(error!);

        return ChatToolResult.Success(
            $"Will switch {InvoiceLookup.Describe(invoice)} back to Draft: status Completed → Draft, the document number is kept. " +
            "It must be issued again with complete_invoice after editing and re-sent if it was already e-mailed.");
    }

    public async Task<ChatToolResult> ExecuteAsync(
        Dictionary<string, string> parameters,
        CancellationToken ct = default)
    {
        // Re-checked: the preview and this call are not paired (see IConfirmableChatTool).
        var (invoice, error) = await LoadRevertibleAsync(parameters, ct);
        if (invoice is null)
            return ChatToolResult.Failure(error!);

        _logger.LogInformation("RevertInvoiceToDraftTool reverting invoice {InvoiceId} to Draft", invoice.Id);

        var reverted = await _invoiceService.RevertToDraftAsync(invoice.Id, ct);
        return reverted is null
            ? ChatToolResult.Failure($"Issued invoice with ID {invoice.Id} not found.")
            : ChatToolResult.Success($"{InvoiceLookup.Describe(reverted)} is now {reverted.Status}. You can edit it with update_invoice.");
    }

    private async Task<(InvoiceDto? Invoice, string? Error)> LoadRevertibleAsync(
        Dictionary<string, string> parameters, CancellationToken ct)
    {
        var (invoice, error) = await InvoiceLookup.ResolveAsync(_invoiceService, parameters, ct);
        if (invoice is null)
            return (null, error);

        return invoice.Status == EInvoiceStatus.Completed
            ? (invoice, null)
            : (null, $"{InvoiceLookup.Describe(invoice)} is {invoice.Status} — only a Completed invoice can be reverted to draft.");
    }
}

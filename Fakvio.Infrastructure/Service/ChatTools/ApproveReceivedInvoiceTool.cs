using Fakvio.Application.Service;
using Microsoft.Extensions.Logging;

namespace Fakvio.Infrastructure.Service.ChatTools;

/// <summary>
/// Chat tool that approves a received invoice for payment — status Received → Approved.
///
/// Typical usage:
///   "Schval přijatou fakturu 267708922"
///   "Approve received invoice ID 123"
///
/// The invoice is identified by <c>id</c> or <c>document_number</c>
/// (see <see cref="ReceivedInvoiceLookup"/>).
///
/// Junior note: this tool writes, but it is NOT behind the confirm gate — the status rule in
/// <see cref="IReceivedInvoiceService.ApproveAsync"/> only lets a freshly received invoice
/// through, the change is small and the assistant reports it in the same turn. The gate is
/// reserved for the destructive delete (issue #218, DEVGUIDE §4.7).
/// </summary>
public class ApproveReceivedInvoiceTool : IChatTool
{
    private readonly IReceivedInvoiceService _receivedInvoiceService;
    private readonly ILogger<ApproveReceivedInvoiceTool> _logger;

    public ApproveReceivedInvoiceTool(
        IReceivedInvoiceService receivedInvoiceService,
        ILogger<ApproveReceivedInvoiceTool> logger)
    {
        _receivedInvoiceService = receivedInvoiceService;
        _logger = logger;
    }

    public string ToolName => "approve_received_invoice";

    public string Description =>
        "Approve a received (incoming/expense) invoice for payment — moves it from status " +
        "'Received' to 'Approved'. Identify the invoice by id or by document number.";

    public IReadOnlyList<ChatToolParameter> Parameters => ReceivedInvoiceLookup.IdentityParameters;

    public async Task<ChatToolResult> ExecuteAsync(
        Dictionary<string, string> parameters,
        CancellationToken ct = default)
    {
        var resolution = await ReceivedInvoiceLookup.ResolveAsync(_receivedInvoiceService, parameters, ct);
        if (resolution.Error is not null)
            return resolution.Error;

        var invoice = resolution.Invoice!;
        _logger.LogInformation("ApproveReceivedInvoiceTool: approving received invoice {Id}", invoice.Id);

        try
        {
            var approved = await _receivedInvoiceService.ApproveAsync(invoice.Id, ct);

            // Null means the record disappeared between the lookup and the write. Rare, but the
            // alternative — reporting success — would tell the user about a change that is not there.
            return approved is null
                ? ChatToolResult.Failure($"Received invoice with ID {invoice.Id} no longer exists — nothing was approved.")
                : ChatToolResult.Success($"Received invoice approved:\n{ReceivedInvoiceLookup.Describe(approved)}");
        }
        catch (InvalidOperationException ex)
        {
            // The status rule from ReceivedInvoiceService. Handled here rather than left to the
            // executor's catch-all, so it is logged as the ordinary refusal it is (not an error)
            // and the model reads the reason without the "Tool execution failed" wrapper.
            _logger.LogInformation("ApproveReceivedInvoiceTool: refused by business rule — {Reason}", ex.Message);
            return ChatToolResult.Failure(ex.Message);
        }
    }
}

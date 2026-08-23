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
/// It changes data, so it goes through the confirm gate (<see cref="IConfirmableChatTool"/>,
/// DEVGUIDE §4.7 rule 7): the first call only names the invoice that would be approved.
///
/// Junior note on why approving in particular is worth a question: it is irreversible. There is
/// no un-approve, and <see cref="IReceivedInvoiceService.DeleteAsync"/> refuses an approved
/// invoice — so a misread "schval to" both changes the state and closes the way back.
/// </summary>
public class ApproveReceivedInvoiceTool : IConfirmableChatTool
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

    /// <summary>
    /// Names the invoice that would be approved, so the user confirms a concrete document.
    /// Reads only — the executor appends the "confirm?" wording itself.
    /// </summary>
    public async Task<ChatToolResult> BuildPreviewAsync(
        Dictionary<string, string> parameters,
        CancellationToken ct = default)
    {
        var resolution = await ReceivedInvoiceLookup.ResolveAsync(_receivedInvoiceService, parameters, ct);
        if (resolution.Error is not null)
            return resolution.Error;

        return ChatToolResult.Success(
            "This would approve the received invoice for payment (status → Approved):\n" +
            ReceivedInvoiceLookup.Describe(resolution.Invoice!));
    }

    public async Task<ChatToolResult> ExecuteAsync(
        Dictionary<string, string> parameters,
        CancellationToken ct = default)
    {
        // Resolved again, deliberately: preview and execution are two independent calls with the
        // user's turns in between (see IConfirmableChatTool), so nothing from the preview is reused.
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

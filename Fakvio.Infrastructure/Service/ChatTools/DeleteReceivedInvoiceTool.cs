using Fakvio.Application.Service;
using Microsoft.Extensions.Logging;

namespace Fakvio.Infrastructure.Service.ChatTools;

/// <summary>
/// Chat tool that deletes a received invoice (soft delete).
///
/// Typical usage:
///   "Smaž přijatou fakturu 267708922"  → preview → user agrees → the model repeats the call
///                                        with confirm: true
///
/// It changes data, so it goes through the confirm gate (<see cref="IConfirmableChatTool"/>,
/// DEVGUIDE §4.7 rule 7) — like every other write in this set. The first call only names the
/// invoice that would disappear.
///
/// Junior note on what the gate is NOT: an authorization boundary. Deleting a received invoice
/// is something the user can already do in the UI — the gate only stops the assistant from doing
/// it silently. The <c>confirm</c> parameter is added by the executor, never declared here
/// (see <see cref="ChatToolConfirmation"/> and DEVGUIDE §4.7).
/// </summary>
public class DeleteReceivedInvoiceTool : IConfirmableChatTool
{
    private readonly IReceivedInvoiceService _receivedInvoiceService;
    private readonly ILogger<DeleteReceivedInvoiceTool> _logger;

    public DeleteReceivedInvoiceTool(
        IReceivedInvoiceService receivedInvoiceService,
        ILogger<DeleteReceivedInvoiceTool> logger)
    {
        _receivedInvoiceService = receivedInvoiceService;
        _logger = logger;
    }

    public string ToolName => "delete_received_invoice";

    public string Description =>
        "Delete a received (incoming/expense) invoice. Only invoices in status 'Received' or " +
        "'Rejected' can be deleted. Identify the invoice by id or by document number.";

    public IReadOnlyList<ChatToolParameter> Parameters => ReceivedInvoiceLookup.IdentityParameters;

    /// <summary>
    /// Names the invoice that would be deleted, so the user confirms a concrete document and not
    /// just the word "delete". Reads only — the executor appends the "confirm?" wording itself.
    /// </summary>
    public async Task<ChatToolResult> BuildPreviewAsync(
        Dictionary<string, string> parameters,
        CancellationToken ct = default)
    {
        var resolution = await ReceivedInvoiceLookup.ResolveAsync(_receivedInvoiceService, parameters, ct);
        if (resolution.Error is not null)
            return resolution.Error;

        return ChatToolResult.Success(
            $"This would delete the received invoice:\n{ReceivedInvoiceLookup.Describe(resolution.Invoice!)}");
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
        _logger.LogInformation("DeleteReceivedInvoiceTool: deleting received invoice {Id}", invoice.Id);

        try
        {
            var deleted = await _receivedInvoiceService.DeleteAsync(invoice.Id, ct);

            return deleted
                ? ChatToolResult.Success($"Received invoice deleted:\n{ReceivedInvoiceLookup.Describe(invoice)}")
                : ChatToolResult.Failure($"Received invoice with ID {invoice.Id} no longer exists — nothing was deleted.");
        }
        catch (InvalidOperationException ex)
        {
            _logger.LogInformation("DeleteReceivedInvoiceTool: refused by business rule — {Reason}", ex.Message);
            return ChatToolResult.Failure(ex.Message);
        }
    }
}

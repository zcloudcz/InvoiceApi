using Fakvio.Application.Service;
using Microsoft.Extensions.Logging;

namespace Fakvio.Infrastructure.Service.ChatTools;

/// <summary>
/// Chat tool that marks a received invoice as paid — status Approved → Paid.
///
/// Typical usage:
///   "Přijatou fakturu 267708922 jsme zaplatili"
///   "Mark received invoice 123 as paid on 2026-04-15"
///
/// The invoice is identified by <c>id</c> or <c>document_number</c>
/// (see <see cref="ReceivedInvoiceLookup"/>). <c>paid_at</c> exists because expenses are
/// usually recorded after the fact — the API endpoint takes the same optional date.
///
/// Junior note: not behind the confirm gate, for the same reason as the approve tool —
/// see <see cref="ApproveReceivedInvoiceTool"/>.
/// </summary>
public class MarkReceivedInvoicePaidTool : IChatTool
{
    private readonly IReceivedInvoiceService _receivedInvoiceService;
    private readonly ILogger<MarkReceivedInvoicePaidTool> _logger;

    public MarkReceivedInvoicePaidTool(
        IReceivedInvoiceService receivedInvoiceService,
        ILogger<MarkReceivedInvoicePaidTool> logger)
    {
        _receivedInvoiceService = receivedInvoiceService;
        _logger = logger;
    }

    public string ToolName => "mark_received_invoice_paid";

    public string Description =>
        "Mark a received (incoming/expense) invoice as paid — moves it from status 'Approved' " +
        "to 'Paid'. Identify the invoice by id or by document number. " +
        "Pass paid_at when the payment happened on a different day than today.";

    private static readonly ChatToolParameter[] Schema =
    [
        .. ReceivedInvoiceLookup.IdentityParameters,
        new ChatToolParameter
        {
            Name = "paid_at",
            Type = ChatToolParameterType.String,
            Description = "Payment date in YYYY-MM-DD format. Defaults to today when omitted."
        }
    ];

    public IReadOnlyList<ChatToolParameter> Parameters => Schema;

    public async Task<ChatToolResult> ExecuteAsync(
        Dictionary<string, string> parameters,
        CancellationToken ct = default)
    {
        // Read the date first: an unreadable one must stop the write, not quietly become today —
        // that would file the payment into the wrong day and the wrong VAT period.
        if (!ChatToolDates.TryParseOptional(parameters, "paid_at", out var paidAt, out var dateError))
            return ChatToolResult.Failure(dateError!);

        var resolution = await ReceivedInvoiceLookup.ResolveAsync(_receivedInvoiceService, parameters, ct);
        if (resolution.Error is not null)
            return resolution.Error;

        var invoice = resolution.Invoice!;
        _logger.LogInformation(
            "MarkReceivedInvoicePaidTool: marking received invoice {Id} as paid, paid_at={PaidAt}",
            invoice.Id, paidAt);

        try
        {
            // Null paid_at is passed through on purpose — the service, not the tool, owns the
            // "paid today" default, so the API and the chat cannot drift apart.
            var paid = await _receivedInvoiceService.MarkAsPaidAsync(invoice.Id, paidAt, ct);

            return paid is null
                ? ChatToolResult.Failure($"Received invoice with ID {invoice.Id} no longer exists — nothing was marked as paid.")
                : ChatToolResult.Success($"Received invoice marked as paid:\n{ReceivedInvoiceLookup.Describe(paid)}");
        }
        catch (InvalidOperationException ex)
        {
            _logger.LogInformation("MarkReceivedInvoicePaidTool: refused by business rule — {Reason}", ex.Message);
            return ChatToolResult.Failure(ex.Message);
        }
    }
}

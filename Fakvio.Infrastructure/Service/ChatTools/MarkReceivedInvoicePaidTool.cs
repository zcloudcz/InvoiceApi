using Fakvio.Application.Service;
using Fakvio.Contracts.Dto.ReceivedInvoice;
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
/// It changes data, so it goes through the confirm gate (<see cref="IConfirmableChatTool"/>,
/// DEVGUIDE §4.7 rule 7) — same as the approve tool, and for the same reason: there is no
/// un-pay either.
/// </summary>
public class MarkReceivedInvoicePaidTool : IConfirmableChatTool
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

    /// <summary>
    /// Names the invoice and the payment date that would be recorded. The date matters here:
    /// approving "mark it paid" without seeing which day is approving a VAT period blindly.
    /// </summary>
    public async Task<ChatToolResult> BuildPreviewAsync(
        Dictionary<string, string> parameters,
        CancellationToken ct = default)
    {
        var (paidAt, invoice, error) = await LoadAsync(parameters, ct);
        if (invoice is null)
            return error!;

        var whenPaid = paidAt is null ? "today" : ChatToolDates.Format(paidAt);

        return ChatToolResult.Success(
            $"This would mark the received invoice as paid on {whenPaid} (status → Paid):\n" +
            ReceivedInvoiceLookup.Describe(invoice));
    }

    public async Task<ChatToolResult> ExecuteAsync(
        Dictionary<string, string> parameters,
        CancellationToken ct = default)
    {
        // Re-read and re-resolve: preview and execution are two independent calls with the user's
        // turns in between (see IConfirmableChatTool), so nothing from the preview is reused.
        var (paidAt, invoice, error) = await LoadAsync(parameters, ct);
        if (invoice is null)
            return error!;

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

    /// <summary>
    /// The reading half both calls share: the payment date and the invoice it belongs to.
    /// Returns a null invoice together with the failure to return, never both or neither.
    /// </summary>
    private async Task<(DateTime? PaidAt, ReceivedInvoiceDto? Invoice, ChatToolResult? Error)> LoadAsync(
        Dictionary<string, string> parameters,
        CancellationToken ct)
    {
        // Date first: an unreadable one must stop the write, not quietly become today —
        // that would file the payment into the wrong day and the wrong VAT period.
        if (!ChatToolDates.TryParseOptional(parameters, "paid_at", out var paidAt, out var dateError))
            return (null, null, ChatToolResult.Failure(dateError!));

        var resolution = await ReceivedInvoiceLookup.ResolveAsync(_receivedInvoiceService, parameters, ct);

        return resolution.Error is not null
            ? (null, null, resolution.Error)
            : (paidAt, resolution.Invoice, null);
    }
}

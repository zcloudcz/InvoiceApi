using System.Globalization;
using Fakvio.Application.Service;
using Fakvio.Contracts.Dto.Invoice;
using Fakvio.Domain.Enums;
using Microsoft.Extensions.Logging;

namespace Fakvio.Infrastructure.Service.ChatTools;

/// <summary>
/// Chat tool that creates the final invoice (vyuctovani) for a paid proforma, deducting the
/// received advance. Mirrors MCP <c>IssueFinalInvoice</c>. The result is a Draft — it is
/// issued with <c>complete_invoice</c>. Data-changing, so it goes through the confirm gate
/// (<see cref="IConfirmableChatTool"/>).
/// </summary>
public class IssueFinalInvoiceTool : IConfirmableChatTool
{
    private readonly IInvoiceService _invoiceService;
    private readonly ILogger<IssueFinalInvoiceTool> _logger;

    public IssueFinalInvoiceTool(IInvoiceService invoiceService, ILogger<IssueFinalInvoiceTool> logger)
    {
        _invoiceService = invoiceService;
        _logger = logger;
    }

    public string ToolName => "issue_final_invoice";

    public string Description =>
        "Create the FINAL invoice for a PAID proforma: repeats the proforma's items and " +
        "deducts the received advance. Omit deduction_amount to deduct the whole remaining advance. " +
        "Creates a Draft; issue it with complete_invoice. Identify the proforma by ID or document number.";

    public IReadOnlyList<ChatToolParameter> Parameters { get; } =
    [
        .. InvoiceLookup.IdentitySchema,
        new()
        {
            Name = "deduction_amount",
            Type = ChatToolParameterType.Number,
            Description = "Advance amount incl. VAT to deduct on this invoice. Omit = whole remaining advance."
        }
    ];

    public async Task<ChatToolResult> BuildPreviewAsync(Dictionary<string, string> parameters, CancellationToken ct = default)
    {
        var (proforma, error) = await LoadProformaAsync(parameters, ct);
        if (proforma is null)
            return ChatToolResult.Failure(error!);

        var amount = ReadDeduction(parameters);
        return ChatToolResult.Success(
            $"Will create a Draft final invoice from {InvoiceLookup.Describe(proforma)}, deducting " +
            (amount.HasValue ? $"{amount:N2}" : "the whole remaining advance") + ".");
    }

    public async Task<ChatToolResult> ExecuteAsync(Dictionary<string, string> parameters, CancellationToken ct = default)
    {
        var (proforma, error) = await LoadProformaAsync(parameters, ct);
        if (proforma is null)
            return ChatToolResult.Failure(error!);

        _logger.LogInformation("IssueFinalInvoiceTool creating final invoice for proforma {ProformaId}", proforma.Id);

        try
        {
            var dto = IssueFinalInvoiceDto.FromProforma(proforma, ReadDeduction(parameters));
            var invoice = await _invoiceService.IssueFinalInvoiceAsync(proforma.Id, dto, ct);
            return ChatToolResult.Success(
                $"Created Draft {invoice.DocumentType} (ID {invoice.Id}), total {invoice.TotalWithVat:N2} " +
                $"{invoice.CurrencyCode}. Issue it with complete_invoice.");
        }
        catch (InvalidOperationException ex)
        {
            // Business-rule refusals (unpaid proforma, deduction above the remaining advance).
            return ChatToolResult.Failure(ex.Message);
        }
    }

    private static decimal? ReadDeduction(Dictionary<string, string> parameters)
        => parameters.TryGetValue("deduction_amount", out var raw)
           && decimal.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out var amount)
            ? amount
            : null;

    private async Task<(InvoiceDto? Proforma, string? Error)> LoadProformaAsync(
        Dictionary<string, string> parameters, CancellationToken ct)
    {
        var (invoice, error) = await InvoiceLookup.ResolveAsync(_invoiceService, parameters, ct);
        if (invoice is null)
            return (null, error);

        return invoice.DocumentType == EDocumentType.Proforma
            ? (invoice, null)
            : (null, $"{InvoiceLookup.Describe(invoice)} is not a Proforma.");
    }
}

using Fakvio.Application.Service;
using Fakvio.Domain.Enums;

namespace Fakvio.Infrastructure.Service.ChatTools;

/// <summary>
/// Chat tool that reports how much of a proforma's received advance is not yet deducted on
/// final invoices. Read-only; mirrors MCP <c>GetRemainingAdvance</c>.
/// </summary>
public class GetRemainingAdvanceTool : IChatTool
{
    private readonly IInvoiceService _invoiceService;

    public GetRemainingAdvanceTool(IInvoiceService invoiceService) => _invoiceService = invoiceService;

    public string ToolName => "get_remaining_advance";

    public string Description =>
        "Get the remaining advance of a PROFORMA: the amount received (incl. VAT) minus what final " +
        "invoices already deducted. Identify the proforma by ID or document number.";

    public IReadOnlyList<ChatToolParameter> Parameters => InvoiceLookup.IdentitySchema;

    public async Task<ChatToolResult> ExecuteAsync(Dictionary<string, string> parameters, CancellationToken ct = default)
    {
        var (proforma, error) = await InvoiceLookup.ResolveAsync(_invoiceService, parameters, ct);
        if (proforma is null)
            return ChatToolResult.Failure(error!);
        if (proforma.DocumentType != EDocumentType.Proforma)
            return ChatToolResult.Failure($"{InvoiceLookup.Describe(proforma)} is not a Proforma.");

        var remaining = await _invoiceService.GetRemainingAdvanceAsync(proforma.Id, ct);
        return ChatToolResult.Success(
            $"{InvoiceLookup.Describe(proforma)}: received {proforma.PaidAmount:N2}, " +
            $"remaining advance to deduct {remaining:N2} {proforma.CurrencyCode}.");
    }
}

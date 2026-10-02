using Fakvio.Application.Service;
using Fakvio.Contracts.Dto.Invoice;
using Fakvio.Domain.Enums;
using Microsoft.Extensions.Logging;

namespace Fakvio.Infrastructure.Service.ChatTools;

/// <summary>
/// Chat tool that issues the tax receipt for advance payment (DPP) of a paid proforma.
/// Mirrors MCP <c>IssueTaxReceipt</c>. Data-changing, so it goes through the confirm gate
/// (<see cref="IConfirmableChatTool"/>).
/// </summary>
public class IssueTaxReceiptTool : IConfirmableChatTool
{
    private readonly IInvoiceService _invoiceService;
    private readonly ILogger<IssueTaxReceiptTool> _logger;

    public IssueTaxReceiptTool(IInvoiceService invoiceService, ILogger<IssueTaxReceiptTool> logger)
    {
        _invoiceService = invoiceService;
        _logger = logger;
    }

    public string ToolName => "issue_tax_receipt";

    public string Description =>
        "Issue a tax receipt for advance payment (DPP) for the received advance of a PROFORMA. " +
        "VAT payers only; normally issued automatically when the proforma is paid. Only the part of " +
        "the advance not yet covered by a DPP is covered. Identify the proforma by ID or document number.";

    public IReadOnlyList<ChatToolParameter> Parameters => InvoiceLookup.IdentitySchema;

    public async Task<ChatToolResult> BuildPreviewAsync(Dictionary<string, string> parameters, CancellationToken ct = default)
    {
        var (proforma, error) = await LoadProformaAsync(parameters, ct);
        return proforma is null
            ? ChatToolResult.Failure(error!)
            : ChatToolResult.Success(
                $"Will issue a tax receipt for advance payment for {InvoiceLookup.Describe(proforma)} " +
                "covering the received advance not yet covered by an existing tax receipt.");
    }

    public async Task<ChatToolResult> ExecuteAsync(Dictionary<string, string> parameters, CancellationToken ct = default)
    {
        var (proforma, error) = await LoadProformaAsync(parameters, ct);
        if (proforma is null)
            return ChatToolResult.Failure(error!);

        _logger.LogInformation("IssueTaxReceiptTool issuing DPP for proforma {ProformaId}", proforma.Id);

        var receipt = await _invoiceService.IssueTaxReceiptForPaidProformaAsync(proforma.Id, null, null, ct);
        return receipt is null
            ? ChatToolResult.Failure(
                "No tax receipt issued: the issuer is not a VAT payer, or the received advance is already fully covered.")
            : ChatToolResult.Success(
                $"Issued {receipt.DocumentType} {receipt.DocumentNumber ?? $"(ID {receipt.Id})"}, " +
                $"{receipt.TotalWithVat:N2} {receipt.CurrencyCode}.");
    }

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

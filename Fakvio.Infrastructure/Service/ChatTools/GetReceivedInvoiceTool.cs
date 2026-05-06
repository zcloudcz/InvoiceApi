using System.Globalization;
using System.Text;
using Fakvio.Application.Service;
using Microsoft.Extensions.Logging;

namespace Fakvio.Infrastructure.Service.ChatTools;

/// <summary>
/// Chat tool that retrieves a single received invoice by its internal ID or document number.
/// Returns all relevant fields: supplier, items, VAT breakdown, totals, status, dates.
///
/// Typical usage:
///   "Ukaž mi přijatou fakturu 267708922"
///   "Detail přijaté faktury č. 2024-0051"
///   "Zobraz přijatou fakturu ID 123"
///
/// Parameters:
///   - id (string, optional): Internal database ID of the received invoice.
///   - document_number (string, optional): Document number printed on the invoice (e.g. "267708922").
///     If both are provided, id takes precedence.
/// </summary>
public class GetReceivedInvoiceTool : IChatTool
{
    private readonly IReceivedInvoiceService _receivedInvoiceService;
    private readonly ILogger<GetReceivedInvoiceTool> _logger;

    public GetReceivedInvoiceTool(
        IReceivedInvoiceService receivedInvoiceService,
        ILogger<GetReceivedInvoiceTool> logger)
    {
        _receivedInvoiceService = receivedInvoiceService;
        _logger = logger;
    }

    public string ToolName => "get_received_invoice";

    public string Description =>
        "Get detail of a received (incoming/expense) invoice by ID or document number. " +
        "Returns all fields: supplier info, line items, VAT breakdown, totals, dates, status, payment info.";

    public string ParameterDescription =>
        "id (string, optional): internal database ID. " +
        "document_number (string, optional): document number as printed on the invoice. " +
        "At least one of id or document_number is required.";

    /// <summary>
    /// Resolves the invoice by id first, then by document_number as fallback,
    /// and formats a complete summary for the AI to present.
    /// </summary>
    public async Task<ChatToolResult> ExecuteAsync(
        Dictionary<string, string> parameters,
        CancellationToken ct = default)
    {
        parameters.TryGetValue("id", out var idStr);
        parameters.TryGetValue("document_number", out var docNumber);

        if (string.IsNullOrWhiteSpace(idStr) && string.IsNullOrWhiteSpace(docNumber))
        {
            return ChatToolResult.Failure(
                "Provide at least one of: id (database ID) or document_number (invoice number).");
        }

        _logger.LogInformation(
            "GetReceivedInvoiceTool: id={Id}, document_number={DocNum}", idStr, docNumber);

        Contracts.Dto.ReceivedInvoice.ReceivedInvoiceDto? invoice = null;

        // Path 1: Lookup by internal ID (most precise).
        if (!string.IsNullOrWhiteSpace(idStr) && long.TryParse(idStr.Trim(), out var id))
        {
            invoice = await _receivedInvoiceService.GetByIdAsync(id, ct);
            if (invoice == null)
                return ChatToolResult.Failure($"Received invoice with ID {id} not found.");
        }
        else if (!string.IsNullOrWhiteSpace(docNumber))
        {
            // Path 2: Search all invoices by document number (case-insensitive contains).
            var trimmed = docNumber.Trim();
            var all = await _receivedInvoiceService.GetAllAsync(ct: ct);
            invoice = all.FirstOrDefault(r =>
                r.DocumentNumber != null &&
                r.DocumentNumber.Contains(trimmed, StringComparison.OrdinalIgnoreCase));

            if (invoice == null)
                return ChatToolResult.Failure(
                    $"Received invoice with document number '{trimmed}' not found.");
        }
        else
        {
            return ChatToolResult.Failure("Invalid id parameter — must be a numeric database ID.");
        }

        return ChatToolResult.Success(FormatInvoice(invoice));
    }

    /// <summary>
    /// Formats a received invoice DTO into a readable text block for the AI.
    /// Includes every significant field so the AI can answer any question about the invoice.
    /// </summary>
    private static string FormatInvoice(Contracts.Dto.ReceivedInvoice.ReceivedInvoiceDto inv)
    {
        var sb = new StringBuilder();
        sb.AppendLine("Received Invoice Detail:");
        sb.AppendLine($"- ID: {inv.Id}");
        sb.AppendLine($"- Document number: {inv.DocumentNumber ?? "(not set)"}");
        sb.AppendLine($"- Status: {inv.Status}");
        sb.AppendLine($"- Supplier: {inv.SupplierName} (ID: {inv.SupplierId})");
        sb.AppendLine($"- Issue date: {FormatDate(inv.IssueDate)}");
        sb.AppendLine($"- Received date: {FormatDate(inv.ReceivedDate)}");
        sb.AppendLine($"- Due date: {FormatDate(inv.DueDate)}");
        sb.AppendLine($"- Taxable supply date (DUZP): {FormatDate(inv.TaxableSupplyDate)}");
        sb.AppendLine($"- Variable symbol: {inv.VariableSymbol ?? "(none)"}");
        sb.AppendLine($"- Currency: {inv.CurrencyCode} ({inv.CurrencySymbol})");
        sb.AppendLine($"- Payment method: {inv.PaymentMethod?.ToString() ?? "(not set)"}");
        sb.AppendLine($"- Bank account: {inv.BankAccountNumber ?? "(none)"}");
        sb.AppendLine($"- IBAN: {inv.IBAN ?? "(none)"}");
        sb.AppendLine($"- SWIFT: {inv.SWIFT ?? "(none)"}");
        sb.AppendLine($"- Paid at: {FormatDate(inv.PaidAt)}");
        sb.AppendLine($"- Notes: {inv.Notes ?? "(none)"}");
        sb.AppendLine();

        // Totals section — the most common source of questions ("proč má špatnou celkovou částku?").
        sb.AppendLine("Totals:");
        sb.AppendLine($"- Total before VAT: {inv.TotalBeforeVat:N2} {inv.CurrencyCode}");
        sb.AppendLine($"- Total VAT: {inv.TotalVat:N2} {inv.CurrencyCode}");
        sb.AppendLine($"- Total with VAT: {inv.TotalWithVat:N2} {inv.CurrencyCode}");
        sb.AppendLine();

        // Line items — crucial for auditing mismatched totals.
        if (inv.Items.Count == 0)
        {
            sb.AppendLine("Line items: (none)");
        }
        else
        {
            sb.AppendLine($"Line items ({inv.Items.Count}):");
            foreach (var item in inv.Items)
            {
                sb.AppendLine(
                    $"  [{item.OrderIndex + 1}] {item.Description} | " +
                    $"qty: {item.Quantity} {item.Unit} | " +
                    $"unit price: {item.UnitPrice:N2} | " +
                    $"VAT: {item.VatRatePercentage}% | " +
                    $"before VAT: {item.TotalBeforeVat:N2} | " +
                    $"VAT amount: {item.VatAmount:N2} | " +
                    $"total: {item.TotalWithVat:N2} {inv.CurrencyCode}");
            }

            // Sanity check: compare sum of items vs invoice-level totals so the AI
            // can immediately spot a discrepancy if asked "proč má špatnou celkovou částku?".
            var sumBeforeVat = inv.Items.Sum(i => i.TotalBeforeVat);
            var sumVat = inv.Items.Sum(i => i.VatAmount);
            var sumWithVat = inv.Items.Sum(i => i.TotalWithVat);

            sb.AppendLine();
            sb.AppendLine("Item totals cross-check:");
            sb.AppendLine(
                $"- Sum of items (before VAT): {sumBeforeVat:N2} " +
                $"[invoice header: {inv.TotalBeforeVat:N2}] " +
                $"— {(sumBeforeVat == inv.TotalBeforeVat ? "MATCH" : "MISMATCH")}");
            sb.AppendLine(
                $"- Sum of items (VAT): {sumVat:N2} " +
                $"[invoice header: {inv.TotalVat:N2}] " +
                $"— {(sumVat == inv.TotalVat ? "MATCH" : "MISMATCH")}");
            sb.AppendLine(
                $"- Sum of items (with VAT): {sumWithVat:N2} " +
                $"[invoice header: {inv.TotalWithVat:N2}] " +
                $"— {(sumWithVat == inv.TotalWithVat ? "MATCH" : "MISMATCH")}");
        }

        sb.AppendLine($"- Created at: {FormatDate(inv.CreatedAt)}");
        sb.AppendLine($"- Updated at: {FormatDate(inv.UpdatedAt)}");

        return sb.ToString();
    }

    private static string FormatDate(DateTime? d)
        => d.HasValue ? d.Value.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) : "(none)";

    private static string FormatDate(DateTime d)
        => d.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
}

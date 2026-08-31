using System.Text;
using Fakvio.Application.Service;
using Fakvio.Contracts.Dto.Invoice;

namespace Fakvio.Infrastructure.Service.ChatTools;

/// <summary>
/// Chat tool that reads one issued (outgoing) invoice, credit note, proforma or advance tax
/// receipt — by internal ID or by the number printed on the document.
///
/// Read-only counterpart of <see cref="GetReceivedInvoiceTool"/>. It covers both MCP tools
/// <c>GetInvoice</c> and <c>FindInvoiceByNumber</c>: the two differ only in the lookup key,
/// and a model that has to pick between two tools for "show me FAK-2026-001" picks wrong
/// about as often as it picks right.
///
/// Typical usage:
///   "Ukaž mi fakturu FAK-2026-001"
///   "Detail faktury ID 42"
/// </summary>
public class GetInvoiceTool : IChatTool
{
    private readonly IInvoiceService _invoiceService;

    public GetInvoiceTool(IInvoiceService invoiceService)
    {
        _invoiceService = invoiceService;
    }

    public string ToolName => "get_invoice";

    public string Description =>
        "Get the full detail of an ISSUED (outgoing) invoice, credit note, proforma or advance " +
        "tax receipt by its ID or document number. Returns line items, VAT breakdown, totals, " +
        "dates, payment state and the payment details printed on the document.";

    public IReadOnlyList<ChatToolParameter> Parameters => InvoiceLookup.IdentitySchema;

    public async Task<ChatToolResult> ExecuteAsync(
        Dictionary<string, string> parameters,
        CancellationToken ct = default)
    {
        var (invoice, error) = await InvoiceLookup.ResolveAsync(_invoiceService, parameters, ct);

        return invoice is null
            ? ChatToolResult.Failure(error!)
            : ChatToolResult.Success(Format(invoice));
    }

    /// <summary>
    /// Renders the invoice as a text block. Every field the assistant is likely to be asked
    /// about is included — the model cannot ask a follow-up question of the database.
    /// </summary>
    private static string Format(InvoiceDto invoice)
    {
        var sb = new StringBuilder();

        sb.AppendLine("Issued invoice detail:");
        sb.AppendLine($"- ID: {invoice.Id}");
        sb.AppendLine($"- Document number: {invoice.DocumentNumber ?? "(not set)"}");
        sb.AppendLine($"- Document type: {invoice.DocumentType}");
        sb.AppendLine($"- Status: {invoice.Status}");
        sb.AppendLine($"- Client: {invoice.ClientName}");
        sb.AppendLine($"- Issuer: {invoice.IssuerName}");
        sb.AppendLine($"- Issue date: {ChatToolDates.Format(invoice.IssueDate)}");
        sb.AppendLine($"- Due date: {ChatToolDates.Format(invoice.DueDate)}");
        sb.AppendLine($"- Taxable supply date (DUZP): {ChatToolDates.Format(invoice.TaxableSupplyDate)}");
        sb.AppendLine($"- Variable symbol: {invoice.VariableSymbol ?? "(none)"}");
        sb.AppendLine($"- Bank account: {invoice.BankAccountNumber ?? "(none)"}");
        sb.AppendLine($"- IBAN: {invoice.IBAN ?? "(none)"}");
        sb.AppendLine($"- Payment method: {invoice.PaymentMethod?.ToString() ?? "(not set)"}");
        sb.AppendLine($"- Paid at: {ChatToolDates.Format(invoice.PaidAt)}");
        sb.AppendLine($"- Paid amount: {invoice.PaidAmount:N2} {invoice.CurrencyCode}");
        sb.AppendLine($"- Sent by e-mail: {(invoice.IsSentByEmail ? ChatToolDates.Format(invoice.LastSentByEmailAt) : "no")}");
        sb.AppendLine($"- Notes: {invoice.Notes ?? "(none)"}");

        // A credit note without its original is unreadable — the number is what the accountant asks for.
        if (invoice.OriginalInvoiceId.HasValue)
            sb.AppendLine($"- Related document: {invoice.OriginalInvoiceNumber ?? "(no number)"} (ID {invoice.OriginalInvoiceId})");

        sb.AppendLine();
        sb.AppendLine("Totals:");
        sb.AppendLine($"- Total before VAT: {invoice.TotalBeforeVat:N2} {invoice.CurrencyCode}");
        sb.AppendLine($"- Total VAT: {invoice.TotalVat:N2} {invoice.CurrencyCode}");
        sb.AppendLine($"- Total with VAT: {invoice.TotalWithVat:N2} {invoice.CurrencyCode}");
        sb.AppendLine();

        if (invoice.InvoiceItem.Count == 0)
        {
            sb.AppendLine("Line items: (none)");
            return sb.ToString();
        }

        sb.AppendLine($"Line items ({invoice.InvoiceItem.Count}):");
        foreach (var item in invoice.InvoiceItem)
        {
            sb.AppendLine(
                $"  [{item.OrderIndex + 1}] {item.Description} | " +
                $"qty: {item.Quantity} {item.Unit} | " +
                $"unit price: {item.UnitPrice:N2} | " +
                $"VAT: {item.VatRatePercentage}% ({item.VatRegime}) | " +
                $"before VAT: {item.TotalBeforeVat:N2} | " +
                $"VAT amount: {item.VatAmount:N2} | " +
                $"total: {item.TotalWithVat:N2} {invoice.CurrencyCode}");
        }

        return sb.ToString();
    }
}

using System.Globalization;
using System.Text;
using Fakvio.Application.Service;
using Microsoft.Extensions.Logging;

namespace Fakvio.Infrastructure.Service.ChatTools;

/// <summary>
/// Chat tool that performs a free-text search across received invoices.
/// Searches by document number, supplier name, variable symbol, and amount.
///
/// Typical usage:
///   "Najdi přijatou fakturu 267708922"
///   "Hledej přijaté faktury od firmy Alza"
///   "Search received invoices amount 15000"
///   "Přijaté faktury variabilní symbol 123456"
///
/// Parameters:
///   - query (string, required): Free-text search string.
///     Matched against: document number (contains), supplier name (contains),
///     variable symbol (contains), amount (exact match or close).
///   - limit (string, optional): Max results to return (default 10, max 50).
/// </summary>
public class SearchReceivedInvoicesTool : IChatTool
{
    private readonly IReceivedInvoiceService _receivedInvoiceService;
    private readonly ILogger<SearchReceivedInvoicesTool> _logger;

    public SearchReceivedInvoicesTool(
        IReceivedInvoiceService receivedInvoiceService,
        ILogger<SearchReceivedInvoicesTool> logger)
    {
        _receivedInvoiceService = receivedInvoiceService;
        _logger = logger;
    }

    public string ToolName => "search_received_invoices";

    public string Description =>
        "Full-text search across received (incoming/expense) invoices. " +
        "Searches by document number, supplier name, variable symbol, or amount. " +
        "Use this when the user provides a number or name without specifying which field.";

    public string ParameterDescription =>
        "query (string, required): search text — matched against document number, supplier name, " +
        "variable symbol, and amount. " +
        "limit (number, optional, default 10, max 50): maximum number of results to return.";

    /// <summary>
    /// Loads all non-deleted invoices and filters in memory using contains search.
    /// This avoids complex query composition for a simple search-all use case.
    ///
    /// Performance note: GetAllAsync returns the full list but is fast for typical tenant sizes
    /// (hundreds to low thousands of received invoices). If performance becomes an issue,
    /// switch to GetPagedAsync with filter.Search (which pushes the filter to the DB).
    /// </summary>
    public async Task<ChatToolResult> ExecuteAsync(
        Dictionary<string, string> parameters,
        CancellationToken ct = default)
    {
        if (!parameters.TryGetValue("query", out var query) || string.IsNullOrWhiteSpace(query))
            return ChatToolResult.Failure("Missing required parameter: query.");

        query = query.Trim();
        var limit = ParseLimit(parameters);

        _logger.LogInformation("SearchReceivedInvoicesTool: query='{Query}', limit={Limit}", query, limit);

        // Use GetPagedAsync with the Search filter — the service already implements
        // contains search across DocumentNumber, Supplier name, and VariableSymbol.
        var filter = new Contracts.Dto.ReceivedInvoice.ReceivedInvoiceFilterDto
        {
            Search = query,
            Page = 1,
            PageSize = limit
        };

        var page = await _receivedInvoiceService.GetPagedAsync(filter, ct);

        // Also try amount matching if the query looks numeric.
        // This lets users say "najdi fakturu za 15000" and find it even without a document number.
        var extraAmountMatches = new List<Contracts.Dto.ReceivedInvoice.ReceivedInvoiceDto>();
        if (decimal.TryParse(query, NumberStyles.Any, CultureInfo.InvariantCulture, out var amount))
        {
            // Load all for amount match — amount is not in the service-level text search.
            var all = await _receivedInvoiceService.GetAllAsync(ct: ct);
            extraAmountMatches = all
                .Where(r => r.TotalWithVat == amount || r.TotalBeforeVat == amount)
                // Deduplicate against what page already returned.
                .Where(r => !page.Items.Any(p => p.Id == r.Id))
                .Take(limit)
                .ToList();
        }

        var results = page.Items.Concat(extraAmountMatches)
            .Take(limit)
            .ToList();

        if (results.Count == 0)
            return ChatToolResult.Success(
                $"No received invoices found matching '{query}'.");

        return ChatToolResult.Success(FormatResults(query, results, page.TotalCount));
    }

    /// <summary>
    /// Formats the search results as a compact text block.
    /// Keeps each row on one line so the AI can quickly present multiple matches.
    /// </summary>
    private static string FormatResults(
        string query,
        List<Contracts.Dto.ReceivedInvoice.ReceivedInvoiceDto> results,
        int totalDbMatches)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"Search results for '{query}' " +
                      $"({results.Count} shown, {totalDbMatches} total matches):");

        foreach (var inv in results)
        {
            sb.AppendLine(
                $"  ID={inv.Id} | {inv.DocumentNumber ?? "(no number)"} | " +
                $"{inv.SupplierName} | " +
                $"{inv.Status} | " +
                $"issued: {FormatDate(inv.IssueDate)} | " +
                $"due: {FormatDate(inv.DueDate)} | " +
                $"total: {inv.TotalWithVat:N2} {inv.CurrencyCode}");
        }

        if (results.Count == 1)
        {
            // Expand detail for single hit — the user most likely wants full info.
            var inv = results[0];
            sb.AppendLine();
            sb.AppendLine("Full detail of the single match:");
            sb.AppendLine($"  Before VAT: {inv.TotalBeforeVat:N2} | VAT: {inv.TotalVat:N2} | With VAT: {inv.TotalWithVat:N2} {inv.CurrencyCode}");
            sb.AppendLine($"  Variable symbol: {inv.VariableSymbol ?? "(none)"}");
            sb.AppendLine($"  Notes: {inv.Notes ?? "(none)"}");
            sb.AppendLine($"  Items: {inv.Items.Count}");

            foreach (var item in inv.Items)
            {
                sb.AppendLine(
                    $"    [{item.OrderIndex + 1}] {item.Description} | " +
                    $"qty: {item.Quantity} {item.Unit} | " +
                    $"unit price: {item.UnitPrice:N2} | " +
                    $"VAT: {item.VatRatePercentage}% | " +
                    $"total: {item.TotalWithVat:N2} {inv.CurrencyCode}");
            }
        }

        return sb.ToString();
    }

    private static int ParseLimit(Dictionary<string, string> p)
    {
        if (p.TryGetValue("limit", out var s) && int.TryParse(s, out var v) && v > 0)
            return Math.Min(v, 50);
        return 10;
    }

    private static string FormatDate(DateTime? d)
        => d.HasValue ? d.Value.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) : "(none)";
}

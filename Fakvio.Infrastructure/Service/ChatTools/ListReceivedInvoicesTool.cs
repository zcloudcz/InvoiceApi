using System.Globalization;
using System.Text;
using Fakvio.Application.Service;
using Fakvio.Contracts.Common.Pagination;
using Fakvio.Contracts.Dto.ReceivedInvoice;
using Fakvio.Domain.Enums;
using Microsoft.Extensions.Logging;

namespace Fakvio.Infrastructure.Service.ChatTools;

/// <summary>
/// Chat tool that returns a paged list of received invoices with optional filtering.
/// Useful for answering questions like:
///   "Ukaž mi všechny přijaté faktury od dodavatele ABC"
///   "Jaké přijaté faktury čekají na schválení?"
///   "Přijaté faktury za duben 2024"
///   "List received invoices over 50000 CZK"
///
/// Parameters:
///   - page (string, optional): Page number (default 1).
///   - page_size (string, optional): Items per page (default 10, max 50).
///   - status (string, optional): Filter by status — Received, Approved, Paid, Rejected.
///   - supplier_name (string, optional): Filter by supplier name (case-insensitive contains).
///   - issue_date_from (string, optional): YYYY-MM-DD start of issue date range.
///   - issue_date_to (string, optional): YYYY-MM-DD end of issue date range.
///   - min_amount (string, optional): Minimum total amount (with VAT).
///   - max_amount (string, optional): Maximum total amount (with VAT).
///   - currency (string, optional): Currency code filter (e.g., CZK, EUR).
///   - overdue (string, optional): "true" — only overdue invoices (Approved + past due date).
/// </summary>
public class ListReceivedInvoicesTool : IChatTool
{
    private readonly IReceivedInvoiceService _receivedInvoiceService;
    private readonly IClientService _clientService;
    private readonly ILogger<ListReceivedInvoicesTool> _logger;

    public ListReceivedInvoicesTool(
        IReceivedInvoiceService receivedInvoiceService,
        IClientService clientService,
        ILogger<ListReceivedInvoicesTool> logger)
    {
        _receivedInvoiceService = receivedInvoiceService;
        _clientService = clientService;
        _logger = logger;
    }

    public string ToolName => "list_received_invoices";

    public string Description =>
        "List received (incoming/expense) invoices with optional filtering by status, " +
        "supplier name, date range, amount range, or currency. Returns paged results with totals.";

    /// <summary>
    /// Parameter schema — static because it never changes per instance.
    /// Every filter is optional: with no parameters the tool returns the first page.
    /// </summary>
    private static readonly ChatToolParameter[] Schema =
    [
        new()
        {
            Name = "page",
            Type = ChatToolParameterType.Integer,
            Description = "Page number (default 1)"
        },
        new()
        {
            Name = "page_size",
            Type = ChatToolParameterType.Integer,
            Description = "Items per page (default 10, max 50)"
        },
        new()
        {
            Name = "status",
            Type = ChatToolParameterType.String,
            Description = "Filter by invoice status",
            AllowedValues = ["Received", "Approved", "Paid", "Rejected"]
        },
        new()
        {
            Name = "supplier_name",
            Type = ChatToolParameterType.String,
            Description = "Supplier company name (case-insensitive substring match)"
        },
        new()
        {
            Name = "issue_date_from",
            Type = ChatToolParameterType.String,
            Description = "Issue date range start in YYYY-MM-DD format"
        },
        new()
        {
            Name = "issue_date_to",
            Type = ChatToolParameterType.String,
            Description = "Issue date range end in YYYY-MM-DD format"
        },
        new()
        {
            Name = "min_amount",
            Type = ChatToolParameterType.Number,
            Description = "Minimum total amount including VAT"
        },
        new()
        {
            Name = "max_amount",
            Type = ChatToolParameterType.Number,
            Description = "Maximum total amount including VAT"
        },
        new()
        {
            Name = "currency",
            Type = ChatToolParameterType.String,
            Description = "ISO 4217 currency code filter (CZK, EUR, …)"
        },
        new()
        {
            Name = "overdue",
            Type = ChatToolParameterType.Boolean,
            Description = "True to return only overdue invoices"
        }
    ];

    public IReadOnlyList<ChatToolParameter> Parameters => Schema;

    /// <summary>
    /// Builds a filter DTO from the AI parameters and calls GetPagedAsync.
    /// Supplier name filter is resolved to a SupplierId so the DB query is efficient.
    /// </summary>
    public async Task<ChatToolResult> ExecuteAsync(
        Dictionary<string, string> parameters,
        CancellationToken ct = default)
    {
        _logger.LogInformation("ListReceivedInvoicesTool executing with parameters: {Params}",
            string.Join(", ", parameters.Select(kv => $"{kv.Key}={kv.Value}")));

        // ── Build filter ───────────────────────────────────────────────────────
        var filter = new ReceivedInvoiceFilterDto
        {
            Page = ParseInt(parameters, "page", 1),
            PageSize = Math.Min(ParseInt(parameters, "page_size", 10), 50)
        };

        // Status filter.
        if (parameters.TryGetValue("status", out var statusStr) &&
            !string.IsNullOrWhiteSpace(statusStr) &&
            Enum.TryParse<EReceivedInvoiceStatus>(statusStr.Trim(), ignoreCase: true, out var status))
        {
            filter.Status = status;
        }

        // Supplier filter — resolve name → supplier ID via client service.
        if (parameters.TryGetValue("supplier_name", out var supplierName) &&
            !string.IsNullOrWhiteSpace(supplierName))
        {
            var supplierId = await ResolveSupplierIdAsync(supplierName.Trim(), ct);
            if (supplierId == null)
                return ChatToolResult.Failure(
                    $"Supplier '{supplierName}' not found in the database.");
            filter.SupplierId = supplierId;
        }

        // Date range filters. Issue #301: this used to have its own TryParseExact copy that
        // returned null on an unreadable date, which silently dropped the filter — "přijaté
        // faktury za březen" then quietly widened to the whole history. Routed through the
        // shared ChatToolDates helper, same as ListInvoicesTool: a present-but-unreadable date
        // fails loudly instead, so the model reads the error and retries with a readable one.
        if (!ChatToolDates.TryParseOptional(parameters, "issue_date_from", out var issueDateFrom, out var dateError) ||
            !ChatToolDates.TryParseOptional(parameters, "issue_date_to", out var issueDateTo, out dateError))
        {
            return ChatToolResult.Failure(dateError!);
        }

        filter.IssueDateFrom = issueDateFrom;
        filter.IssueDateTo = issueDateTo;

        // Amount range filters.
        if (parameters.TryGetValue("min_amount", out var minStr) &&
            decimal.TryParse(minStr, NumberStyles.Any, CultureInfo.InvariantCulture, out var min))
            filter.MinAmount = min;

        if (parameters.TryGetValue("max_amount", out var maxStr) &&
            decimal.TryParse(maxStr, NumberStyles.Any, CultureInfo.InvariantCulture, out var max))
            filter.MaxAmount = max;

        // Currency filter.
        if (parameters.TryGetValue("currency", out var currency) && !string.IsNullOrWhiteSpace(currency))
            filter.Currency = currency.Trim().ToUpper();

        // Overdue flag.
        if (parameters.TryGetValue("overdue", out var overdueStr) &&
            overdueStr.Equals("true", StringComparison.OrdinalIgnoreCase))
            filter.IsOverdue = true;

        // ── Execute query ────────────────────────────────────────────────────
        var result = await _receivedInvoiceService.GetPagedAsync(filter, ct);

        return ChatToolResult.Success(FormatPage(result, filter));
    }

    /// <summary>
    /// Resolves a supplier name to a DB client ID (case-insensitive substring match).
    /// Returns null if zero or multiple matches are found (ambiguous).
    /// </summary>
    private async Task<long?> ResolveSupplierIdAsync(string name, CancellationToken ct)
    {
        var clients = await _clientService.GetAllClientsAsync(cancellationToken: ct);
        var matches = clients
            .Where(c => c.IsActive && c.CompanyName != null &&
                        c.CompanyName.Contains(name, StringComparison.OrdinalIgnoreCase))
            .ToList();

        return matches.Count == 1 ? matches[0].Id : null;
    }

    /// <summary>
    /// Formats the paged result into a text block for the AI to present.
    /// Shows summary totals and a row per invoice (compact, one line each).
    /// </summary>
    private static string FormatPage(
        PagedResult<ReceivedInvoiceDto> page,
        ReceivedInvoiceFilterDto filter)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"Received invoices (page {page.PageNumber}/{Math.Max(1, (int)Math.Ceiling((double)page.TotalCount / page.PageSize))}, " +
                      $"total: {page.TotalCount}):");

        if (page.Items.Count == 0)
        {
            sb.AppendLine("  No records match the specified filters.");
            return sb.ToString();
        }

        // Summary totals for the current page, grouped by currency (issue #269) — a plain
        // Sum() across mixed-currency pages produces a number with no unit and no real-world
        // meaning. Shared with ListInvoicesTool so the two sibling tools stay in the same format.
        sb.AppendLine($"  Page total (with VAT): " +
                      $"{ChatToolTotals.FormatPageTotal(page.Items, r => r.TotalWithVat, r => r.CurrencyCode)}");
        sb.AppendLine();

        // One compact row per invoice.
        foreach (var inv in page.Items)
        {
            sb.AppendLine(
                $"  ID={inv.Id} | {inv.DocumentNumber ?? "(no number)"} | " +
                $"{inv.SupplierName} | " +
                $"{inv.Status} | " +
                $"issued: {FormatDate(inv.IssueDate)} | " +
                $"due: {FormatDate(inv.DueDate)} | " +
                $"total: {inv.TotalWithVat:N2} {inv.CurrencyCode}");
        }

        return sb.ToString();
    }

    // ─── Parsing helpers ─────────────────────────────────────────────────────

    private static int ParseInt(Dictionary<string, string> p, string key, int defaultValue)
    {
        if (p.TryGetValue(key, out var s) && int.TryParse(s, out var v) && v > 0)
            return v;
        return defaultValue;
    }

    private static string FormatDate(DateTime? d)
        => d.HasValue ? d.Value.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) : "(none)";
}

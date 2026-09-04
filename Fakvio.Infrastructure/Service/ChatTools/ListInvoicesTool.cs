using System.Text;
using Fakvio.Application.Service;
using Fakvio.Contracts.Common.Pagination;
using Fakvio.Contracts.Dto.Invoice;
using Fakvio.Domain.Enums;
using Microsoft.Extensions.Logging;

namespace Fakvio.Infrastructure.Service.ChatTools;

/// <summary>
/// Chat tool that lists issued (outgoing) invoices and credit notes with optional filtering.
/// Read-only counterpart of <see cref="ListReceivedInvoicesTool"/>, which covers přijaté faktury.
///
/// One tool covers all three reporting cuts asked for in issue #228, because they are the same
/// query with different filters:
///   "Které faktury jsou po splatnosti?"        → overdue = true
///   "Faktury pro klienta Alza"                 → client_name = "Alza"
///   "Vydané faktury za březen 2026"            → issue_date_from / issue_date_to
///   "Kolik dobropisů jsme vystavili letos?"    → document_type = CreditNote + date range
/// </summary>
public class ListInvoicesTool : IChatTool
{
    /// <summary>Upper bound on rows per call — more than this only floods the model's context.</summary>
    private const int MaxPageSize = 50;

    private const int DefaultPageSize = 10;

    private readonly IInvoiceService _invoiceService;
    private readonly ITenantResolver _tenantResolver;
    private readonly ILogger<ListInvoicesTool> _logger;

    public ListInvoicesTool(
        IInvoiceService invoiceService,
        ITenantResolver tenantResolver,
        ILogger<ListInvoicesTool> logger)
    {
        _invoiceService = invoiceService;
        _tenantResolver = tenantResolver;
        _logger = logger;
    }

    public string ToolName => "list_invoices";

    public string Description =>
        "List issued (outgoing) invoices and credit notes with optional filtering by status, " +
        "document type, client name, issue date range, or overdue flag. Returns paged results " +
        "with page totals. Use it for overdue receivables, per-client history, and period reports.";

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
            Description = "Filter by invoice status. Combine with overdue to list partially paid arrears.",
            AllowedValues = ["Draft", "Completed", "Paid", "PartiallyPaid", "Creditnoted"]
        },
        new()
        {
            Name = "document_type",
            Type = ChatToolParameterType.String,
            Description = "Filter by document type",
            AllowedValues = ["Invoice", "CreditNote", "Proforma", "TaxReceiptForAdvance"]
        },
        new()
        {
            Name = "client_name",
            Type = ChatToolParameterType.String,
            Description = "Client company name (case-insensitive substring match)"
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
            Name = "overdue",
            Type = ChatToolParameterType.Boolean,
            Description = "True to return only invoices past their due date (unpaid receivables)"
        }
    ];

    public IReadOnlyList<ChatToolParameter> Parameters => Schema;

    /// <summary>
    /// Builds an <see cref="InvoiceFilterDto"/> from the AI parameters and runs the paged query.
    /// Presence, allowed values and types were already validated by ChatToolExecutor, so this
    /// method only translates values and applies the reporting defaults.
    /// </summary>
    public async Task<ChatToolResult> ExecuteAsync(
        Dictionary<string, string> parameters,
        CancellationToken ct = default)
    {
        _logger.LogInformation("ListInvoicesTool executing with parameters: {Params}",
            string.Join(", ", parameters.Select(kv => $"{kv.Key}={kv.Value}")));

        // The schema can only say "string", so the FORMAT of a date is validated here. A value
        // that is present but unreadable is rejected rather than dropped — the same reasoning
        // (and wording) as GetVatReportTool: a report for the wrong period reads as plausibly
        // as the right one.
        if (!ChatToolDates.TryParseOptional(parameters, "issue_date_from", out var issueDateFrom, out var dateError) ||
            !ChatToolDates.TryParseOptional(parameters, "issue_date_to", out var issueDateTo, out dateError))
        {
            return ChatToolResult.Failure(dateError!);
        }

        var filter = new InvoiceFilterDto
        {
            Page = ParsePositiveInt(parameters, "page", 1),
            PageSize = Math.Min(ParsePositiveInt(parameters, "page_size", DefaultPageSize), MaxPageSize),

            // Scope to the signed-in issuer, exactly like InvoiceController does. Null
            // (SysAdmin without impersonation) leaves the query at tenant scope.
            IssuerId = _tenantResolver.GetCurrentCompanyId(),

            IssueDateFrom = issueDateFrom,
            IssueDateTo = issueDateTo
        };

        if (parameters.TryGetValue("status", out var statusText) &&
            Enum.TryParse<EInvoiceStatus>(statusText.Trim(), ignoreCase: true, out var status))
        {
            filter.Status = status;
        }

        if (parameters.TryGetValue("document_type", out var documentTypeText) &&
            Enum.TryParse<EDocumentType>(documentTypeText.Trim(), ignoreCase: true, out var documentType))
        {
            filter.DocumentType = documentType;
        }

        if (parameters.TryGetValue("client_name", out var clientName) && !string.IsNullOrWhiteSpace(clientName))
            filter.ClientName = clientName.Trim();

        // No local Trim needed here: ChatToolExecutor normalizes every parameter value
        // (trims it) before dispatch, so the raw dictionary value is already clean (#268).
        var overdueOnly = parameters.TryGetValue("overdue", out var overdueText) &&
                          overdueText.Equals("true", StringComparison.OrdinalIgnoreCase);

        if (overdueOnly)
            ApplyOverdueReporting(filter);

        var page = await _invoiceService.GetInvoicesPagedAsync(filter, ct);

        return ChatToolResult.Success(FormatPage(page, overdueOnly));
    }

    /// <summary>
    /// Turns "overdue" into the same definition the dashboard uses: a Completed invoice past its
    /// due date (<c>DashboardService.GetDashboardAsync</c>). Without the status default the raw
    /// filter would also return drafts with an old due date — documents that were never sent to
    /// anyone and therefore owe nothing, which would silently inflate every arrears answer.
    ///
    /// The caller can still override it (e.g. status = PartiallyPaid) — the filter DTO only
    /// supports one status at a time, so "Completed OR PartiallyPaid" needs two calls.
    /// </summary>
    private static void ApplyOverdueReporting(InvoiceFilterDto filter)
    {
        filter.IsOverdue = true;
        filter.Status ??= EInvoiceStatus.Completed;

        // Most overdue first — that is the order anyone chasing payments wants to read.
        // SortBy is matched against ENTITY property names by InvoiceService, hence nameof on the entity.
        filter.SortBy = nameof(Domain.Entities.Invoice.DueDate);
        filter.SortDirection = "asc";
    }

    /// <summary>
    /// Formats the paged result into a text block for the AI to present.
    /// Shows the page total and one compact row per document.
    /// </summary>
    private static string FormatPage(PagedResult<InvoiceDto> page, bool overdueOnly)
    {
        var sb = new StringBuilder();
        var pageCount = Math.Max(1, (int)Math.Ceiling((double)page.TotalCount / page.PageSize));

        sb.AppendLine($"{(overdueOnly ? "Overdue issued invoices" : "Issued invoices")} " +
                      $"(page {page.PageNumber}/{pageCount}, total: {page.TotalCount}):");

        if (page.Items.Count == 0)
        {
            sb.AppendLine("  No records match the specified filters.");
            return sb.ToString();
        }

        sb.AppendLine($"  Page total (with VAT): {page.Items.Sum(i => i.TotalWithVat):N2}");
        sb.AppendLine();

        foreach (var invoice in page.Items)
        {
            sb.AppendLine(
                $"  ID={invoice.Id} | {invoice.DocumentNumber ?? "(no number)"} | " +
                $"{invoice.DocumentType} | {invoice.ClientName} | {invoice.Status} | " +
                $"issued: {ChatToolDates.Format(invoice.IssueDate)} | " +
                $"due: {ChatToolDates.Format(invoice.DueDate)} | " +
                $"total: {invoice.TotalWithVat:N2} {invoice.CurrencyCode}");
        }

        return sb.ToString();
    }

    /// <summary>
    /// Reads a positive integer parameter. The executor already guaranteed the value parses as
    /// an integer, so the only thing left to reject is zero / negative, which would make the
    /// paging helper throw.
    /// </summary>
    private static int ParsePositiveInt(Dictionary<string, string> parameters, string key, int defaultValue)
        => parameters.TryGetValue(key, out var raw) && int.TryParse(raw, out var value) && value > 0
            ? value
            : defaultValue;
}

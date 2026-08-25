using System.Text;
using Fakvio.Application.Service;
using Fakvio.Contracts.Common.Pagination;
using Fakvio.Contracts.Dto.PaymentMatching;
using Fakvio.Domain.Enums;
using Microsoft.Extensions.Logging;

namespace Fakvio.Infrastructure.Service.ChatTools;

/// <summary>
/// Chat tool that lists bank payments (transactions imported from the bank) — issue #227.
///
/// Typical usage:
///   "Které platby ještě nejsou spárované?"
///   "Přišly nám nějaké peníze tento týden?"
///   "Platby od ABC za březen"
///
/// Read-only by decision of story #149: matching and un-matching a payment to an invoice moves
/// money between documents, and the Payments page shows the candidates and the remaining
/// amounts that make such a decision safe. The assistant reads, the user matches.
/// </summary>
public class ListPaymentsTool : IChatTool
{
    /// <summary>Page size used when the model does not ask for one. Small on purpose — the result is read aloud.</summary>
    private const int DefaultPageSize = 10;

    private readonly IBankTransactionQueryService _bankTransactionQueryService;
    private readonly ILogger<ListPaymentsTool> _logger;

    public ListPaymentsTool(
        IBankTransactionQueryService bankTransactionQueryService,
        ILogger<ListPaymentsTool> logger)
    {
        _bankTransactionQueryService = bankTransactionQueryService;
        _logger = logger;
    }

    public string ToolName => "list_payments";

    public string Description =>
        "List bank payments (transactions imported from the bank) with optional filtering by " +
        "matching status, direction (incoming/outgoing), date range, or a free-text search over " +
        "the counterparty, message and variable symbol. Returns paged results. Read-only — " +
        "matching a payment to an invoice is done by the user on the Payments page.";

    /// <summary>
    /// Parameter schema — static because it never changes per instance.
    /// Every filter is optional: with no parameters the tool returns the first page.
    ///
    /// The two closed lists are derived from their enums, so a new matching status or direction
    /// cannot silently disappear from what the model is allowed to ask for.
    /// </summary>
    private static readonly ChatToolParameter[] Schema =
    [
        new()
        {
            Name = "status",
            Type = ChatToolParameterType.String,
            Description = "Filter by matching status (Unmatched = waiting to be paired with an invoice)",
            AllowedValues = Enum.GetNames<EMatchStatus>()
        },
        new()
        {
            Name = "direction",
            Type = ChatToolParameterType.String,
            Description = "Filter by direction: Incoming = money received, Outgoing = money sent",
            AllowedValues = Enum.GetNames<EPaymentDirection>()
        },
        new()
        {
            Name = "search",
            Type = ChatToolParameterType.String,
            Description = "Free text matched against counterparty name/account, message and variable symbol"
        },
        new()
        {
            Name = "date_from",
            Type = ChatToolParameterType.String,
            Description = "Transaction date range start in YYYY-MM-DD format"
        },
        new()
        {
            Name = "date_to",
            Type = ChatToolParameterType.String,
            Description = "Transaction date range end in YYYY-MM-DD format"
        },
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
            Description = $"Items per page (default {DefaultPageSize})"
        }
    ];

    public IReadOnlyList<ChatToolParameter> Parameters => Schema;

    public async Task<ChatToolResult> ExecuteAsync(
        Dictionary<string, string> parameters,
        CancellationToken ct = default)
    {
        _logger.LogInformation("ListPaymentsTool executing with parameters: {Params}",
            string.Join(", ", parameters.Select(pair => $"{pair.Key}={pair.Value}")));

        var filter = new BankTransactionFilterDto();

        if (Enum.TryParse<EMatchStatus>(parameters.GetValueOrDefault("status"), ignoreCase: true, out var status))
            filter.Status = status;

        if (Enum.TryParse<EPaymentDirection>(parameters.GetValueOrDefault("direction"), ignoreCase: true, out var direction))
            filter.Direction = direction;

        var search = parameters.GetValueOrDefault("search");
        if (!string.IsNullOrWhiteSpace(search))
            filter.Search = search.Trim();

        // An unreadable date is an error, never a silently dropped filter: dropping it widens
        // "payments in March" to the whole history and the model reports that as the answer.
        if (!ChatToolDates.TryParseOptional(parameters, "date_from", out var dateFrom, out var fromError))
            return ChatToolResult.Failure(fromError!);

        if (!ChatToolDates.TryParseOptional(parameters, "date_to", out var dateTo, out var toError))
            return ChatToolResult.Failure(toError!);

        filter.From = dateFrom;
        filter.To = dateTo;

        var paging = new PaginationParams { PageSize = DefaultPageSize };

        // The executor already validated the types, so a value that is present parses; the
        // guard on > 0 only stops a nonsensical page from reaching the query.
        if (int.TryParse(parameters.GetValueOrDefault("page"), out var page) && page > 0)
            paging.Page = page;

        // PageSize clamps itself to 1..100 inside PaginationParams, so no extra check here.
        if (int.TryParse(parameters.GetValueOrDefault("page_size"), out var pageSize))
            paging.PageSize = pageSize;

        var result = await _bankTransactionQueryService.ListAsync(filter, paging, ct);

        return ChatToolResult.Success(Format(result));
    }

    /// <summary>
    /// Formats the paged result into a text block for the AI to present: a header with the
    /// totals and one compact line per payment.
    /// </summary>
    private static string Format(PagedResult<BankTransactionDto> page)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"Payments (page {page.PageNumber}/{Math.Max(1, page.TotalPages)}, total: {page.TotalCount}):");

        if (page.Items.Count == 0)
        {
            sb.AppendLine("  No payments match the specified filters.");
            return sb.ToString();
        }

        foreach (var payment in page.Items)
        {
            sb.AppendLine(
                $"  ID={payment.Id} | {ChatToolDates.Format(payment.TransactionDate)} | " +
                $"{PaymentChatToolSupport.FormatSignedAmount(payment)} | " +
                $"{payment.MatchStatus} | " +
                $"counterparty: {payment.CounterpartyName ?? payment.CounterpartyAccount ?? "(unknown)"} | " +
                $"VS: {payment.VariableSymbol ?? "(none)"}" +
                PaymentChatToolSupport.FormatMatchSuffix(payment));
        }

        sb.AppendLine("Use get_payment with the ID for the full detail of one payment.");

        return sb.ToString();
    }
}

using System.Globalization;
using System.Text;
using Fakvio.Application.Service;
using Fakvio.Contracts.Common.Pagination;
using Fakvio.Contracts.Dto.Reminder;
using Fakvio.Domain.Enums;
using Microsoft.Extensions.Logging;

namespace Fakvio.Infrastructure.Service.ChatTools;

/// <summary>
/// Chat tool that lists payment reminders (dunning) — issue #227.
///
/// Typical usage:
///   "Jaké upomínky čekají na odeslání?"
///   "Komu jsme letos poslali upomínku?"
///   "Upomínky k faktuře 2026-0042"
///
/// Read-only: it answers what the dunning process has produced. Sending, cancelling and
/// generating reminders stay in the UI — the chat side of story #149 covers reading the state
/// and changing the settings, nothing else.
/// </summary>
public class ListRemindersTool : IChatTool
{
    /// <summary>Page size used when the model does not ask for one. Small on purpose — the result is read aloud.</summary>
    private const int DefaultPageSize = 10;

    private readonly IReminderService _reminderService;
    private readonly ILogger<ListRemindersTool> _logger;

    public ListRemindersTool(IReminderService reminderService, ILogger<ListRemindersTool> logger)
    {
        _reminderService = reminderService;
        _logger = logger;
    }

    public string ToolName => "list_reminders";

    public string Description =>
        "List payment reminders (dunning) with optional filtering by status, escalation level, " +
        "date range, or a free-text search over the invoice number, client name and notes. " +
        "Returns paged results. Read-only — use it to answer what has been reminded and what is " +
        "still waiting to be sent.";

    /// <summary>
    /// Parameter schema — static because it never changes per instance.
    /// Every filter is optional: with no parameters the tool returns the first page.
    ///
    /// The status list is derived from the enum, so a new reminder status cannot silently
    /// disappear from what the model is allowed to ask for.
    /// </summary>
    private static readonly ChatToolParameter[] Schema =
    [
        new()
        {
            Name = "status",
            Type = ChatToolParameterType.String,
            Description = "Filter by reminder status",
            AllowedValues = Enum.GetNames<EReminderStatus>()
        },
        new()
        {
            Name = "level",
            Type = ChatToolParameterType.Integer,
            Description = "Filter by escalation level (1 = first reminder)"
        },
        new()
        {
            Name = "search",
            Type = ChatToolParameterType.String,
            Description = "Free text matched against invoice number, client name and notes"
        },
        new()
        {
            Name = "date_from",
            Type = ChatToolParameterType.String,
            Description = "Reminder date range start in YYYY-MM-DD format"
        },
        new()
        {
            Name = "date_to",
            Type = ChatToolParameterType.String,
            Description = "Reminder date range end in YYYY-MM-DD format"
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
        _logger.LogInformation("ListRemindersTool executing with parameters: {Params}",
            string.Join(", ", parameters.Select(pair => $"{pair.Key}={pair.Value}")));

        var filter = new ReminderFilterDto { PageSize = DefaultPageSize };

        // The executor already validated the types, so a value that is present parses; the
        // guard on > 0 only stops a nonsensical page from reaching the query.
        if (int.TryParse(parameters.GetValueOrDefault("page"), out var page) && page > 0)
            filter.Page = page;

        // PageSize clamps itself to 1..100 inside PaginationParams, so no extra check here.
        if (int.TryParse(parameters.GetValueOrDefault("page_size"), out var pageSize))
            filter.PageSize = pageSize;

        if (Enum.TryParse<EReminderStatus>(parameters.GetValueOrDefault("status"), ignoreCase: true, out var status))
            filter.Status = status;

        if (int.TryParse(parameters.GetValueOrDefault("level"), out var level))
            filter.Level = level;

        var search = parameters.GetValueOrDefault("search");
        if (!string.IsNullOrWhiteSpace(search))
            filter.Search = search.Trim();

        // An unreadable date is an error, never a silently dropped filter: dropping it widens
        // "reminders in March" to the whole history and the model reports that as the answer.
        if (!ChatToolDates.TryParseOptional(parameters, "date_from", out var dateFrom, out var fromError))
            return ChatToolResult.Failure(fromError!);

        if (!ChatToolDates.TryParseOptional(parameters, "date_to", out var dateTo, out var toError))
            return ChatToolResult.Failure(toError!);

        filter.DateFrom = dateFrom;
        filter.DateTo = dateTo;

        var result = await _reminderService.GetRemindersPagedAsync(filter, ct);

        return ChatToolResult.Success(Format(result));
    }

    /// <summary>
    /// Formats the paged result into a text block for the AI to present: a header with the
    /// totals and one compact line per reminder.
    /// </summary>
    private static string Format(PagedResult<ReminderDto> page)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"Reminders (page {page.PageNumber}/{Math.Max(1, page.TotalPages)}, total: {page.TotalCount}):");

        if (page.Items.Count == 0)
        {
            sb.AppendLine("  No reminders match the specified filters.");
            return sb.ToString();
        }

        foreach (var reminder in page.Items)
        {
            sb.AppendLine(
                $"  ID={reminder.Id} | invoice {reminder.InvoiceNumber ?? "(no number)"} | " +
                $"{reminder.ClientName ?? "(unknown client)"} | level {reminder.Level} | {reminder.Status} | " +
                $"due: {ChatToolDates.Format(reminder.DueDate)} | " +
                $"reminded: {ChatToolDates.Format(reminder.ReminderDate)} | " +
                $"total due: {reminder.TotalCzk.ToString("N2", CultureInfo.InvariantCulture)} CZK" +
                FormatOutcome(reminder));
        }

        return sb.ToString();
    }

    /// <summary>
    /// Appends what happened to the reminder, but only when there is something to say —
    /// a failed send has to name the error, otherwise the model reports "Failed" with no reason.
    /// </summary>
    private static string FormatOutcome(ReminderDto reminder) => reminder.Status switch
    {
        EReminderStatus.Sent => $" | sent to: {reminder.SentToEmail ?? "(unknown address)"}",
        EReminderStatus.Failed => $" | error: {reminder.ErrorMessage ?? "(no detail)"}",
        EReminderStatus.Cancelled when !string.IsNullOrWhiteSpace(reminder.Notes) => $" | note: {reminder.Notes}",
        _ => string.Empty
    };
}

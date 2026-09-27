using System.ComponentModel;
using System.Text.Json;
using Fakvio.Contracts.Dto.Reminder;
using Fakvio.Domain.Enums;
using Fakvio.McpServer.Client;
using ModelContextProtocol.Server;

namespace Fakvio.McpServer.Tools;

/// <summary>
/// MCP tools for bank payments and payment reminders (dunning) — N3.5. Read-only by the same
/// decision as the chat tools: matching a payment to an invoice moves money between documents,
/// so the model reads and the user matches on the Payments page (DEVGUIDE §4.7).
/// </summary>
[McpServerToolType]
public static class PaymentTools
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        WriteIndented = true,
        Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter() }
    };

    /// <summary>
    /// Lists bank payments (transactions imported from the bank) with optional filters.
    /// </summary>
    [McpServerTool(Title = "List payments", ReadOnly = true, Idempotent = true, OpenWorld = false), Description(
        "List bank payments (transactions imported from the bank), with optional filtering by " +
        "matching status, direction, and date range. Read-only — matching a payment to an " +
        "invoice is done by the user on the Payments page.")]
    public static async Task<string> ListPayments(
        IFakvioApiClient api,
        [Description("Filter by matching status, e.g. 'Unmatched' (waiting to be paired with an invoice)")] string? status = null,
        [Description("Filter by direction: 'Incoming' (money received) or 'Outgoing' (money sent)")] string? direction = null,
        [Description("Transaction date range start, ISO 8601 (e.g. '2026-01-01')")] string? from = null,
        [Description("Transaction date range end, ISO 8601")] string? to = null,
        [Description("Page number (1-based, default 1)")] int page = 1,
        [Description("Items per page (default 20, max 100)")] int pageSize = 20,
        CancellationToken ct = default)
    {
        EMatchStatus? parsedStatus = null;
        if (!string.IsNullOrWhiteSpace(status))
        {
            if (!Enum.TryParse<EMatchStatus>(status, ignoreCase: true, out var s))
                return Error($"Unknown status '{status}'. Valid values: " + string.Join(", ", Enum.GetNames<EMatchStatus>()) + ".");
            parsedStatus = s;
        }

        EPaymentDirection? parsedDirection = null;
        if (!string.IsNullOrWhiteSpace(direction))
        {
            if (!Enum.TryParse<EPaymentDirection>(direction, ignoreCase: true, out var d))
                return Error($"Unknown direction '{direction}'. Valid values: " + string.Join(", ", Enum.GetNames<EPaymentDirection>()) + ".");
            parsedDirection = d;
        }

        DateTime? parsedFrom = null;
        if (!string.IsNullOrWhiteSpace(from))
        {
            if (!DateTime.TryParse(from, out var f))
                return Error($"Invalid from date '{from}'. Use ISO 8601.");
            parsedFrom = f;
        }

        DateTime? parsedTo = null;
        if (!string.IsNullOrWhiteSpace(to))
        {
            if (!DateTime.TryParse(to, out var t))
                return Error($"Invalid to date '{to}'. Use ISO 8601.");
            parsedTo = t;
        }

        try
        {
            var result = await api.GetPaymentsPagedAsync(
                parsedStatus, parsedDirection, parsedFrom, parsedTo, page, Math.Min(pageSize, 100), ct);
            return JsonSerializer.Serialize(result, JsonOptions);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            return McpToolError.ToJson(ex);
        }
    }

    /// <summary>Gets a single bank payment (transaction) by ID.</summary>
    [McpServerTool(Title = "Get payment", ReadOnly = true, Idempotent = true, OpenWorld = false), Description(
        "Get a single bank payment (transaction) by ID. Returns full details including " +
        "matched invoices and counterparty info.")]
    public static async Task<string> GetPayment(
        IFakvioApiClient api,
        [Description("The payment (bank transaction) ID")] long id,
        CancellationToken ct = default)
    {
        try
        {
            var result = await api.GetPaymentByIdAsync(id, ct);
            if (result is null)
                return Error($"Payment with ID {id} not found.");

            return JsonSerializer.Serialize(result, JsonOptions);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            return McpToolError.ToJson(ex);
        }
    }

    /// <summary>Lists payment reminders (dunning), optionally scoped to one invoice.</summary>
    [McpServerTool(Title = "List reminders", ReadOnly = true, Idempotent = true, OpenWorld = false), Description(
        "List payment reminders (upomínky) sent for overdue invoices. Filter by invoiceId to see " +
        "the escalation history of one invoice, or by status.")]
    public static async Task<string> ListReminders(
        IFakvioApiClient api,
        [Description("Show only reminders for this invoice, ordered by escalation level")] long? invoiceId = null,
        [Description("Filter by status: 'Draft', 'Sent', 'Failed', 'Cancelled'")] string? status = null,
        [Description("Page number (1-based, default 1)")] int page = 1,
        [Description("Items per page (default 20, max 100)")] int pageSize = 20,
        CancellationToken ct = default)
    {
        EReminderStatus? parsedStatus = null;
        if (!string.IsNullOrWhiteSpace(status))
        {
            if (!Enum.TryParse<EReminderStatus>(status, ignoreCase: true, out var s))
                return Error($"Unknown status '{status}'. Valid values: " + string.Join(", ", Enum.GetNames<EReminderStatus>()) + ".");
            parsedStatus = s;
        }

        try
        {
            // A single invoice's reminder history is a small, ordered list from its own
            // endpoint — the paged/filtered endpoint sorts and paginates differently and would
            // be the wrong tool for "show me the escalation history of this invoice".
            if (invoiceId.HasValue && parsedStatus is null)
            {
                var byInvoice = await api.GetRemindersByInvoiceAsync(invoiceId.Value, ct);
                return JsonSerializer.Serialize(byInvoice, JsonOptions);
            }

            var filter = new ReminderFilterDto
            {
                InvoiceId = invoiceId,
                Status = parsedStatus,
                Page = page,
                PageSize = Math.Min(pageSize, 100)
            };

            var result = await api.GetRemindersPagedAsync(filter, ct);
            return JsonSerializer.Serialize(result, JsonOptions);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            return McpToolError.ToJson(ex);
        }
    }

    /// <summary>Gets the company-wide default reminder settings.</summary>
    [McpServerTool(Title = "Get reminder settings", ReadOnly = true, Idempotent = true, OpenWorld = false), Description(
        "Get the company's default reminder (dunning) settings: whether reminders are enabled, " +
        "grace period, escalation levels with their fees and interest.")]
    public static async Task<string> GetReminderSettings(
        IFakvioApiClient api,
        CancellationToken ct = default)
    {
        try
        {
            var result = await api.GetReminderSettingsAsync(ct);
            return JsonSerializer.Serialize(result, JsonOptions);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            return McpToolError.ToJson(ex);
        }
    }

    private static string Error(string message) =>
        JsonSerializer.Serialize(new { error = message }, JsonOptions);
}

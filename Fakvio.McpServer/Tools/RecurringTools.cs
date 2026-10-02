using System.ComponentModel;
using System.Text.Json;
using Fakvio.Contracts.Dto.RecurringInvoice;
using Fakvio.McpServer.Client;
using ModelContextProtocol.Server;

namespace Fakvio.McpServer.Tools;

/// <summary>
/// MCP tools for recurring invoice schedules (DEVGUIDE §4.13).
///
/// Junior note: a schedule does NOT create draft invoices waiting for review. When the
/// worker's cycle hits a schedule's NextRunAt, it immediately issues the invoice
/// (Status = Completed, a real document number is consumed) — there is no separate
/// "complete" step like with CreateInvoiceFromTemplate. AutoSend only controls whether that
/// already-issued invoice is also e-mailed to the client, not whether it gets issued.
/// If the worker missed a run (app was down), it catches up ONE invoice per cycle dated on
/// the originally planned day, not "today" — so a long outage does not dump a burst of
/// invoices at once.
/// </summary>
[McpServerToolType]
public static class RecurringTools
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        WriteIndented = true,
        Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter() }
    };

    /// <summary>Lists recurring schedules, optionally filtered to one template.</summary>
    [McpServerTool(Title = "List recurring schedules", ReadOnly = true, Idempotent = true, OpenWorld = false), Description(
        "List recurring invoice schedules — automatic rules that issue an invoice on a repeating " +
        "cadence (Weekly/Monthly/Quarterly/Yearly) for a template + client pair. Optionally filter " +
        "to schedules of one template. Each result includes nextRunAt (next planned issue date), " +
        "lastRunAt, isActive, and lastError (set when the last attempt failed).")]
    public static async Task<string> ListRecurringSchedules(
        IFakvioApiClient api,
        [Description("Only schedules for this invoice template (optional)")] long? templateId = null,
        CancellationToken ct = default)
    {
        try
        {
            var schedules = await api.GetRecurringSchedulesAsync(templateId, ct);
            return JsonSerializer.Serialize(schedules, JsonOptions);
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

    /// <summary>Gets a single recurring schedule by ID.</summary>
    [McpServerTool(Title = "Get recurring schedule", ReadOnly = true, Idempotent = true, OpenWorld = false), Description(
        "Get a single recurring invoice schedule by ID — its frequency, next/last run dates, " +
        "occurrence count/cap, end date, and last error if the last attempt failed.")]
    public static async Task<string> GetRecurringSchedule(
        IFakvioApiClient api,
        [Description("The recurring schedule ID")] long scheduleId,
        CancellationToken ct = default)
    {
        try
        {
            var schedule = await api.GetRecurringScheduleByIdAsync(scheduleId, ct);

            if (schedule is null)
                return JsonSerializer.Serialize(new { error = $"Recurring schedule with ID {scheduleId} not found." }, JsonOptions);

            return JsonSerializer.Serialize(schedule, JsonOptions);
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

    /// <summary>Creates a new recurring schedule on a template.</summary>
    [McpServerTool(Title = "Create recurring schedule", ReadOnly = false, Destructive = false, Idempotent = false, OpenWorld = false), Description(
        "Create a recurring invoice schedule: from startDate onward, an invoice for the given " +
        "template + client is automatically ISSUED (Status = Completed, not a draft) every " +
        "intervalCount-th period of frequency. Monthly/Quarterly/Yearly fire on dayOfMonth (1-28 " +
        "only — no 'last day of month' semantics); Weekly fires on dayOfWeek instead and " +
        "dayOfMonth must be omitted. Optional endDate and/or maxOccurrences stop it automatically. " +
        "autoSend only controls whether the issued invoice is also e-mailed to the client — it is " +
        "issued either way. A missed run (app downtime) is caught up one invoice per hourly worker " +
        "cycle, dated on the originally planned day. shiftPeriodsInText (default true) moves billing periods " +
        "in the template text (\"Hosting 3/2026\", \"březen 2026\", \"Q1 2026\") forward with each generated invoice — " +
        "the template text must then name the period of the FIRST generated invoice.")]
    public static async Task<string> CreateRecurringSchedule(
        IFakvioApiClient api,
        [Description("Schedule to create — templateId, clientId, frequency and startDate are required")]
        CreateRecurringInvoiceScheduleDto schedule,
        CancellationToken ct = default)
    {
        if (schedule is null)
            return JsonSerializer.Serialize(new { error = "schedule is required." }, JsonOptions);

        try
        {
            var result = await api.CreateRecurringScheduleAsync(schedule, ct);
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

    /// <summary>Updates fields of an existing recurring schedule. Null fields are left unchanged.</summary>
    [McpServerTool(Title = "Update recurring schedule", ReadOnly = false, Destructive = false, Idempotent = true, OpenWorld = false), Description(
        "Update an existing recurring schedule. Only provided (non-null) fields change. " +
        "Changing frequency/intervalCount/dayOfMonth/dayOfWeek does NOT recompute nextRunAt — it " +
        "only applies from the next successful run onward. To switch TO Weekly: set " +
        "frequency='Weekly', dayOfMonth=-1 (clears it) and dayOfWeek. To switch AWAY from Weekly " +
        "(to Monthly/Quarterly/Yearly): set the new frequency, dayOfMonth (1-28) and " +
        "clearDayOfWeek=true (dayOfWeek itself is ignored for clearing — you must use " +
        "clearDayOfWeek). clearEndDate / clearMaxOccurrences explicitly remove those limits (plain " +
        "null keeps the current value). nextRunAt lets you explicitly reschedule the next issue date. " +
        "shiftPeriodsInText=true switches period shifting in texts on — the current template text is then taken as the period of the NEXT invoice.")]
    public static async Task<string> UpdateRecurringSchedule(
        IFakvioApiClient api,
        [Description("The recurring schedule ID to update")] long scheduleId,
        [Description("Fields to change — only provided (non-null) fields are updated")]
        UpdateRecurringInvoiceScheduleDto changes,
        CancellationToken ct = default)
    {
        if (changes is null)
            return JsonSerializer.Serialize(new { error = "changes is required." }, JsonOptions);

        try
        {
            var result = await api.UpdateRecurringScheduleAsync(scheduleId, changes, ct);
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

    /// <summary>Pauses a schedule (IsActive = false) without deleting it or its history.</summary>
    [McpServerTool(Title = "Pause recurring schedule", ReadOnly = false, Destructive = false, Idempotent = true, OpenWorld = false), Description(
        "Pause a recurring schedule — the worker stops issuing invoices for it until resumed. " +
        "History (past invoices, occurrence count) is kept. Calling this on an already-paused " +
        "schedule is a no-op.")]
    public static async Task<string> PauseRecurringSchedule(
        IFakvioApiClient api,
        [Description("The recurring schedule ID to pause")] long scheduleId,
        CancellationToken ct = default)
    {
        try
        {
            var result = await api.PauseRecurringScheduleAsync(scheduleId, ct);
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

    /// <summary>Resumes a paused schedule (IsActive = true).</summary>
    [McpServerTool(Title = "Resume recurring schedule", ReadOnly = false, Destructive = false, Idempotent = true, OpenWorld = false), Description(
        "Resume a paused recurring schedule. Rejected if the schedule already reached its end " +
        "condition (endDate passed or maxOccurrences already produced) — update it first to move " +
        "that limit before resuming. Calling this on an already-active schedule is a no-op.")]
    public static async Task<string> ResumeRecurringSchedule(
        IFakvioApiClient api,
        [Description("The recurring schedule ID to resume")] long scheduleId,
        CancellationToken ct = default)
    {
        try
        {
            var result = await api.ResumeRecurringScheduleAsync(scheduleId, ct);
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

    /// <summary>
    /// Deletes a schedule. Hard-deletes if it never issued an invoice, otherwise deactivates it
    /// so the history of already-issued invoices stays intact (see IRecurringInvoiceService.DeleteAsync).
    /// </summary>
    [McpServerTool(Title = "Delete recurring schedule", ReadOnly = false, Destructive = true, Idempotent = false, OpenWorld = false), Description(
        "Delete a recurring schedule. If it never issued an invoice yet, it is permanently removed. " +
        "If it already issued at least one invoice, it is deactivated instead (same effect as " +
        "pause) so the invoice history stays intact — the schedule itself then keeps showing up as " +
        "inactive rather than disappearing.")]
    public static async Task<string> DeleteRecurringSchedule(
        IFakvioApiClient api,
        [Description("The recurring schedule ID to delete")] long scheduleId,
        CancellationToken ct = default)
    {
        try
        {
            await api.DeleteRecurringScheduleAsync(scheduleId, ct);
            return JsonSerializer.Serialize(new { success = true, scheduleId }, JsonOptions);
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
}

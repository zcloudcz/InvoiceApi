using Fakvio.Application.Exceptions;
using Fakvio.Application.Service;
using Fakvio.Contracts.Dto.InvoiceTemplate;
using Fakvio.Contracts.Dto.RecurringInvoice;
using Fakvio.Domain.Entities;
using Fakvio.Domain.Enums;
using Fakvio.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using ZMapper;

namespace Fakvio.Infrastructure.Service;

/// <summary>
/// Implementation of IRecurringInvoiceService — CRUD for schedules plus the generation cycle.
///
/// Owner decisions (2026-W39, overriding the original story spec):
///   - Generated invoices are issued straight away (Status = Completed) — the document number
///     is consumed immediately, so a deleted draft never leaves a hole in the sequence.
///     AutoSend only controls whether the invoice is additionally e-mailed (see N4.5).
///   - A missed period (the app was down when NextRunAt passed) is caught up with exactly ONE
///     invoice per cycle, dated on the ORIGINAL planned date (NextRunAt), not "now". The
///     schedule then advances one period at a time on each subsequent cycle until it catches
///     up to the present — it does not try to generate every missed period at once.
///   - Idempotence: two RunCycleAsync calls covering the same due schedule must create only
///     ONE invoice. This is guaranteed by SaveChanges/commit atomically moving NextRunAt
///     forward together with the generated invoice — the second call re-reads NextRunAt and
///     no longer sees the schedule as due. Cross-instance exclusion is the advisory lock in
///     RecurringInvoiceWorker; this method alone is not safe against true concurrent callers
///     on the SAME schedule (not needed — the worker serializes via the lock).
/// </summary>
public class RecurringInvoiceService : IRecurringInvoiceService
{
    /// <summary>LastError is capped to match the DB column (HasMaxLength(2000)).</summary>
    private const int MaxErrorLength = 2000;

    private readonly TenantDbContext _context;
    private readonly IInvoiceTemplateService _templateService;
    private readonly INotificationService _notificationService;
    private readonly IEmailService _emailService;
    private readonly ILogger<RecurringInvoiceService> _logger;

    public RecurringInvoiceService(
        TenantDbContext context,
        IInvoiceTemplateService templateService,
        INotificationService notificationService,
        IEmailService emailService,
        ILogger<RecurringInvoiceService> logger)
    {
        _context = context;
        _templateService = templateService;
        _notificationService = notificationService;
        _emailService = emailService;
        _logger = logger;
    }

    // ─── Read ────────────────────────────────────────────────────────────────

    public async Task<List<RecurringInvoiceScheduleDto>> GetByTemplateAsync(long templateId, CancellationToken ct = default)
    {
        var schedules = await _context.RecurringInvoiceSchedule
            .AsNoTracking()
            .Include(s => s.Template)
            .Include(s => s.Client)
            .Where(s => s.TemplateId == templateId)
            .OrderBy(s => s.NextRunAt)
            .ToListAsync(ct);

        return schedules.Select(MapToDto).ToList();
    }

    public async Task<List<RecurringInvoiceScheduleDto>> GetAllAsync(CancellationToken ct = default)
    {
        var schedules = await _context.RecurringInvoiceSchedule
            .AsNoTracking()
            .Include(s => s.Template)
            .Include(s => s.Client)
            .OrderBy(s => s.NextRunAt)
            .ToListAsync(ct);

        return schedules.Select(MapToDto).ToList();
    }

    public async Task<RecurringInvoiceScheduleDto?> GetByIdAsync(long id, CancellationToken ct = default)
    {
        var schedule = await _context.RecurringInvoiceSchedule
            .AsNoTracking()
            .Include(s => s.Template)
            .Include(s => s.Client)
            .FirstOrDefaultAsync(s => s.Id == id, ct);

        return schedule is null ? null : MapToDto(schedule);
    }

    private static RecurringInvoiceScheduleDto MapToDto(RecurringInvoiceSchedule entity)
    {
        var dto = entity.ToRecurringInvoiceScheduleDto();
        dto.TemplateName = entity.Template?.Name;
        dto.ClientName = entity.Client?.CompanyName;
        return dto;
    }

    // ─── Write ───────────────────────────────────────────────────────────────

    public async Task<RecurringInvoiceScheduleDto> CreateAsync(CreateRecurringInvoiceScheduleDto createDto, CancellationToken ct = default)
    {
        var template = await _context.Set<InvoiceTemplate>()
            .FirstOrDefaultAsync(t => t.Id == createDto.TemplateId, ct)
            ?? throw new InvalidOperationException($"Template with ID {createDto.TemplateId} not found.");

        if (!template.IsActive)
            throw new InvalidOperationException($"Template '{template.Name}' is not active.");

        var client = await _context.Client.FindAsync(new object[] { createDto.ClientId }, ct)
            ?? throw new InvalidOperationException($"Client with ID {createDto.ClientId} not found.");

        ValidateRecurrenceRule(createDto.Frequency, createDto.IntervalCount, createDto.DayOfMonth, createDto.DayOfWeek);

        if (createDto.EndDate.HasValue && createDto.EndDate.Value <= createDto.StartDate)
            throw new InvalidOperationException("EndDate must be after StartDate.");

        var schedule = new RecurringInvoiceSchedule
        {
            TemplateId = createDto.TemplateId,
            ClientId = createDto.ClientId,
            Frequency = createDto.Frequency,
            IntervalCount = createDto.IntervalCount,
            DayOfMonth = createDto.DayOfMonth,
            DayOfWeek = createDto.DayOfWeek,
            NextRunAt = createDto.StartDate,
            EndDate = createDto.EndDate,
            MaxOccurrences = createDto.MaxOccurrences,
            AutoSend = createDto.AutoSend,
            IsActive = true,
        };

        _context.RecurringInvoiceSchedule.Add(schedule);
        await _context.SaveChangesAsync(ct);

        _logger.LogInformation(
            "Created recurring schedule {ScheduleId} for template {TemplateId}, client {ClientId}, first run {NextRunAt}",
            schedule.Id, schedule.TemplateId, schedule.ClientId, schedule.NextRunAt);

        // Re-load with navigation properties for the denormalized names.
        return (await GetByIdAsync(schedule.Id, ct))!;
    }

    public async Task<RecurringInvoiceScheduleDto> UpdateAsync(long id, UpdateRecurringInvoiceScheduleDto updateDto, CancellationToken ct = default)
    {
        var schedule = await _context.RecurringInvoiceSchedule.FirstOrDefaultAsync(s => s.Id == id, ct)
            ?? throw new InvalidOperationException($"Recurring schedule with ID {id} not found.");

        if (updateDto.ClientId.HasValue)
        {
            var client = await _context.Client.FindAsync(new object[] { updateDto.ClientId.Value }, ct)
                ?? throw new InvalidOperationException($"Client with ID {updateDto.ClientId.Value} not found.");
            schedule.ClientId = client.Id;
        }

        var frequency = updateDto.Frequency ?? schedule.Frequency;
        var intervalCount = updateDto.IntervalCount ?? schedule.IntervalCount;
        var dayOfMonth = updateDto.DayOfMonth switch
        {
            -1 => null,
            not null => updateDto.DayOfMonth,
            null => schedule.DayOfMonth,
        };
        var dayOfWeek = updateDto.ClearDayOfWeek ? null : updateDto.DayOfWeek ?? schedule.DayOfWeek;

        ValidateRecurrenceRule(frequency, intervalCount, dayOfMonth, dayOfWeek);

        var endDate = updateDto.ClearEndDate ? null : updateDto.EndDate ?? schedule.EndDate;
        if (endDate.HasValue && endDate.Value <= (updateDto.NextRunAt ?? schedule.NextRunAt))
            throw new InvalidOperationException("EndDate must be after NextRunAt.");

        schedule.Frequency = frequency;
        schedule.IntervalCount = intervalCount;
        schedule.DayOfMonth = dayOfMonth;
        schedule.DayOfWeek = dayOfWeek;
        schedule.EndDate = endDate;
        schedule.MaxOccurrences = updateDto.ClearMaxOccurrences ? null : updateDto.MaxOccurrences ?? schedule.MaxOccurrences;
        schedule.AutoSend = updateDto.AutoSend ?? schedule.AutoSend;
        if (updateDto.NextRunAt.HasValue)
            schedule.NextRunAt = updateDto.NextRunAt.Value;

        // Editing EndDate/MaxOccurrences down to a value already satisfied by the schedule's
        // current progress must stop it — otherwise it would sit there "active" forever without
        // ever being picked up again by RunCycleAsync (NextRunAt is already past EndDate, or
        // OccurrenceCount already reached MaxOccurrences), which is confusing in the UI.
        if (HasReachedItsEnd(schedule))
            schedule.IsActive = false;

        await _context.SaveChangesAsync(ct);

        return (await GetByIdAsync(schedule.Id, ct))!;
    }

    public async Task<RecurringInvoiceScheduleDto> SetActiveAsync(long id, bool isActive, CancellationToken ct = default)
    {
        var schedule = await _context.RecurringInvoiceSchedule.FirstOrDefaultAsync(s => s.Id == id, ct)
            ?? throw new InvalidOperationException($"Recurring schedule with ID {id} not found.");

        // Resuming a schedule that already exhausted its EndDate/MaxOccurrences would let the
        // worker generate one more invoice before deactivating it again — reject it explicitly
        // instead (Codex review finding #5).
        if (isActive && HasReachedItsEnd(schedule))
            throw new InvalidOperationException(
                "Cannot resume this schedule — it already reached its end date or occurrence limit. " +
                "Change EndDate/MaxOccurrences first if you want it to run again.");

        schedule.IsActive = isActive;
        await _context.SaveChangesAsync(ct);

        return (await GetByIdAsync(schedule.Id, ct))!;
    }

    /// <summary>True when the schedule's NextRunAt/OccurrenceCount already satisfy its own termination condition.</summary>
    private static bool HasReachedItsEnd(RecurringInvoiceSchedule schedule) =>
        (schedule.EndDate.HasValue && schedule.NextRunAt >= schedule.EndDate.Value) ||
        (schedule.MaxOccurrences.HasValue && schedule.OccurrenceCount >= schedule.MaxOccurrences.Value);

    public async Task DeleteAsync(long id, CancellationToken ct = default)
    {
        var schedule = await _context.RecurringInvoiceSchedule.FirstOrDefaultAsync(s => s.Id == id, ct)
            ?? throw new InvalidOperationException($"Recurring schedule with ID {id} not found.");

        if (schedule.OccurrenceCount == 0)
        {
            // Never fired — safe to hard-delete, nothing references it yet.
            _context.RecurringInvoiceSchedule.Remove(schedule);
        }
        else
        {
            // Has generated invoices before — deactivate instead, preserving the audit trail
            // (LastRunAt/OccurrenceCount/generated invoices still reference this schedule's history).
            schedule.IsActive = false;
        }

        await _context.SaveChangesAsync(ct);
    }

    // ─── Validation ──────────────────────────────────────────────────────────

    /// <summary>
    /// Shared validation for create and update — mirrors the entity's documented invariants
    /// (DEVGUIDE §4.13): DayOfMonth 1-28 required for non-Weekly, DayOfWeek only for Weekly.
    /// </summary>
    private static void ValidateRecurrenceRule(
        ERecurrenceFrequency frequency, int intervalCount, int? dayOfMonth, DayOfWeek? dayOfWeek)
    {
        if (intervalCount < 1)
            throw new InvalidOperationException("IntervalCount must be at least 1.");

        if (frequency == ERecurrenceFrequency.Weekly)
        {
            if (dayOfWeek is null)
                throw new InvalidOperationException("DayOfWeek is required when Frequency is Weekly.");
            if (dayOfMonth is not null)
                throw new InvalidOperationException("DayOfMonth must not be set when Frequency is Weekly.");
        }
        else
        {
            if (dayOfMonth is null or < 1 or > 28)
                throw new InvalidOperationException("DayOfMonth (1-28) is required when Frequency is not Weekly.");
            if (dayOfWeek is not null)
                throw new InvalidOperationException("DayOfWeek must not be set when Frequency is not Weekly.");
        }
    }

    // ─── Generation cycle ──────────────────────────────────────────────────────

    public async Task<int> RunCycleAsync(long companyId, DateTimeOffset nowUtc, CancellationToken ct = default)
    {
        var dueSchedules = await _context.RecurringInvoiceSchedule
            .Where(s => s.IsActive && s.NextRunAt <= nowUtc)
            .Select(s => s.Id)
            .ToListAsync(ct);

        var generated = 0;

        foreach (var scheduleId in dueSchedules)
        {
            ct.ThrowIfCancellationRequested();

            if (await RunOneScheduleAsync(scheduleId, companyId, nowUtc, ct))
                generated++;
        }

        return generated;
    }

    /// <summary>
    /// Generates (at most) one invoice for a single due schedule, in its own DB transaction so
    /// a mid-flight failure rolls back the invoice AND the schedule advance together — never
    /// half of one. Returns true if an invoice was generated.
    /// </summary>
    private async Task<bool> RunOneScheduleAsync(long scheduleId, long companyId, DateTimeOffset nowUtc, CancellationToken ct)
    {
        // TenantDbContext is configured with EnableRetryOnFailure. With that setting EF Core
        // refuses a plain BeginTransactionAsync ("NpgsqlRetryingExecutionStrategy does not support
        // user-initiated transactions") — the transaction must run INSIDE the execution strategy.
        // On a transient DB error (dropped connection, failover) the strategy re-runs the WHOLE
        // delegate from scratch, so everything the delegate needs is (re)read inside it:
        //   - the tracker is cleared and the schedule re-read on every attempt, so a retry never
        //     works with in-memory changes left over from the failed attempt;
        //   - if an earlier attempt DID commit (connection dropped right after COMMIT), the
        //     re-read sees NextRunAt already advanced → "not due" → no second invoice.
        // Cross-instance exclusion stays with the advisory lock in RecurringInvoiceWorker, which
        // lives on its own dedicated connection and is not affected by these retries.
        var strategy = _context.Database.CreateExecutionStrategy();

        (long InvoiceId, bool AutoSend)? generated;
        try
        {
            generated = await strategy.ExecuteAsync(
                cancel => GenerateInTransactionAsync(scheduleId, nowUtc, cancel), ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            // The transaction was already rolled back ("await using" disposes it without a commit).
            // The rollback only undoes the DB — tracked entities (schedule.OccurrenceCount++,
            // LastRunAt, …) still carry their in-memory mutations. Clear the tracker so
            // RecordFailureAsync re-fetches the SCHEDULE AS IT ACTUALLY IS IN THE DATABASE,
            // not the half-mutated instance that was rolled back (Codex review finding #1).
            _context.ChangeTracker.Clear();

            // NextRunAt deliberately NOT advanced — the same period is retried next cycle.
            await RecordFailureAsync(scheduleId, companyId, ex, ct);
            return false;
        }

        if (generated is null)
            return false; // Deactivated / already handled since we listed it.

        // E-mailing happens AFTER the commit and fully OUTSIDE the transaction's try/catch —
        // it cannot be "rolled back" (a sent e-mail cannot be unsent), and a cancellation while
        // sending must not attempt to roll back a transaction that was already committed above
        // (Codex review finding #3). TrySendGeneratedInvoiceEmailAsync has its own error handling.
        if (generated.Value.AutoSend)
        {
            await TrySendGeneratedInvoiceEmailAsync(scheduleId, generated.Value.InvoiceId, companyId, ct);
        }

        return true;
    }

    /// <summary>
    /// One attempt of the generation unit (called by the execution strategy, possibly several
    /// times — see RunOneScheduleAsync). Returns null when the schedule is no longer due.
    /// </summary>
    private async Task<(long InvoiceId, bool AutoSend)?> GenerateInTransactionAsync(
        long scheduleId, DateTimeOffset nowUtc, CancellationToken ct)
    {
        // Clear the tracker before every attempt — re-fetching on the same DbContext instance
        // still keeps every previously touched entity (invoices, items, other schedules)
        // tracked, which both grows unboundedly across a big tenant cycle AND can resurrect
        // stale in-memory state from a rolled-back attempt.
        _context.ChangeTracker.Clear();

        var schedule = await _context.RecurringInvoiceSchedule.FirstOrDefaultAsync(s => s.Id == scheduleId, ct);
        if (schedule is null || !schedule.IsActive || schedule.NextRunAt > nowUtc)
            return null;

        // Disposing without CommitAsync (exception, cancellation) rolls the transaction back.
        await using var transaction = await _context.Database.BeginTransactionAsync(ct);

        var plannedDate = schedule.NextRunAt;

        var invoice = await _templateService.CreateInvoiceFromTemplateAsync(
            schedule.TemplateId,
            new CreateInvoiceFromTemplateDto
            {
                ClientId = schedule.ClientId,
                // Dohnat zmeškanou periodu: datum vystavení = PLÁNOVANÉ datum, ne "teď".
                IssueDate = plannedDate.UtcDateTime,
                // Owner decision: worker always issues (Completed) — the document number is
                // consumed immediately, there is no "draft with a hole in the sequence".
                AutoComplete = true,
                // Billing periods in the template text ("Hosting 3/2026") advance with each generated invoice.
                ShiftMonths = RecurrenceCalculator.PeriodShiftMonths(schedule.Frequency, schedule.IntervalCount, schedule.OccurrenceCount),
            },
            ct);

        schedule.OccurrenceCount++;
        schedule.LastRunAt = nowUtc;
        schedule.LastError = null;
        schedule.NextRunAt = RecurrenceCalculator.Next(
            plannedDate, schedule.Frequency, schedule.IntervalCount, schedule.DayOfMonth, schedule.DayOfWeek);

        if (HasReachedItsEnd(schedule))
            schedule.IsActive = false;

        await _context.SaveChangesAsync(ct);
        await transaction.CommitAsync(ct);

        _logger.LogInformation(
            "RecurringInvoice: schedule {ScheduleId} generated invoice {InvoiceId}, next run {NextRunAt}",
            schedule.Id, invoice.Id, schedule.NextRunAt);

        return (invoice.Id, schedule.AutoSend);
    }

    /// <summary>
    /// AutoSend follow-up (N4.5): e-mails the just-generated invoice to the client. The invoice
    /// itself is already committed at this point — a failure here (no e-mail on file, SMTP
    /// error, …) is recorded as LastError + notification like any other failure, but
    /// deliberately does NOT touch NextRunAt/OccurrenceCount: the invoice exists, only the
    /// e-mail didn't go out, so the next period must not be skipped or duplicated.
    /// </summary>
    private async Task TrySendGeneratedInvoiceEmailAsync(long scheduleId, long invoiceId, long companyId, CancellationToken ct)
    {
        try
        {
            var clientId = await _context.RecurringInvoiceSchedule
                .Where(s => s.Id == scheduleId)
                .Select(s => s.ClientId)
                .FirstOrDefaultAsync(ct);

            var client = await _context.Client
                .Include(c => c.Contact)
                .FirstOrDefaultAsync(c => c.Id == clientId, ct);

            var email = client?.Contact
                .Where(c => c.ContactType == EContactType.Email)
                .Select(c => c.ContactValue)
                .FirstOrDefault();

            if (string.IsNullOrEmpty(email))
            {
                await RecordEmailFailureAsync(scheduleId, companyId,
                    $"Invoice {invoiceId} was issued, but AutoSend could not e-mail it: client has no e-mail address on file.", ct);
                return;
            }

            await _emailService.SendInvoiceEmailAsync(invoiceId, email, ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "RecurringInvoice: schedule {ScheduleId} generated invoice {InvoiceId} but AutoSend e-mail failed", scheduleId, invoiceId);
            await RecordEmailFailureAsync(scheduleId, companyId,
                $"Invoice {invoiceId} was issued, but AutoSend could not e-mail it: {SafeErrorMessage(ex)}", ct);
        }
    }

    /// <summary>Same LastError/notification plumbing as RecordFailureAsync, but for the e-mail step alone.</summary>
    private async Task RecordEmailFailureAsync(long scheduleId, long companyId, string message, CancellationToken ct)
    {
        try
        {
            var schedule = await _context.RecurringInvoiceSchedule.FirstOrDefaultAsync(s => s.Id == scheduleId, ct);
            if (schedule is null)
                return;

            schedule.LastError = message.Length > MaxErrorLength ? message[..MaxErrorLength] : message;
            await _context.SaveChangesAsync(ct);

            await _notificationService.CreateForAllUsersAsync(
                ENotificationType.RecurringInvoiceFailed,
                "Odeslání opakované faktury e-mailem selhalo",
                message,
                schedule.Id,
                "RecurringInvoiceSchedule",
                companyId,
                ct);
        }
        catch (Exception notifyEx)
        {
            _logger.LogError(notifyEx, "RecurringInvoice: failed to record e-mail LastError/notification for schedule {ScheduleId}", scheduleId);
        }
    }

    /// <summary>
    /// Writes LastError on the schedule (own SaveChanges, outside the rolled-back transaction)
    /// and raises a notification. A logging failure here must not crash the whole cycle either.
    /// </summary>
    private async Task RecordFailureAsync(long scheduleId, long companyId, Exception ex, CancellationToken ct)
    {
        _logger.LogError(ex, "RecurringInvoice: schedule {ScheduleId} generation failed", scheduleId);

        try
        {
            var schedule = await _context.RecurringInvoiceSchedule.FirstOrDefaultAsync(s => s.Id == scheduleId, ct);
            if (schedule is null)
                return;

            var message = SafeErrorMessage(ex);

            // Notify only when the error state CHANGES (first failure, or a different error than
            // last time). A permanently broken schedule (template or client deleted) is retried
            // every hour; without this check every user of the tenant would get the same
            // notification every hour, forever. The error stays visible in LastError, and a
            // successful run clears LastError — so a failure after a recovery notifies again.
            var isNewError = schedule.LastError != message;

            schedule.LastError = message;
            await _context.SaveChangesAsync(ct);

            if (!isNewError)
                return;

            await _notificationService.CreateForAllUsersAsync(
                ENotificationType.RecurringInvoiceFailed,
                "Generování opakované faktury selhalo",
                $"Naplánovaná faktura ze šablony se nepodařilo vygenerovat: {message}",
                schedule.Id,
                "RecurringInvoiceSchedule",
                companyId,
                ct);
        }
        catch (Exception notifyEx)
        {
            _logger.LogError(notifyEx, "RecurringInvoice: failed to record LastError/notification for schedule {ScheduleId}", scheduleId);
        }
    }

    /// <summary>
    /// Sanitizes an exception message before it is stored in LastError, exposed through the
    /// REST API, shown in the UI, and broadcast in a notification to every user of the tenant
    /// (DEVGUIDE's "no raw exception details leave the server" rule, same reasoning as MCP's
    /// #279 error convention). <see cref="InvalidOperationException"/> and
    /// <see cref="TenantNotReadyException"/> are the two exception types OUR OWN domain code
    /// throws with an already-safe, user-facing message (e.g. "Client with ID 5 not found",
    /// or a readiness issue description) — anything else (DbUpdateException, NpgsqlException,
    /// SmtpCommandException, SocketException, …) can carry connection strings, stack details,
    /// or internal identifiers, so it is replaced with a generic message. The full exception is
    /// always logged server-side (see the LogError calls around every call site).
    /// </summary>
    private static string SafeErrorMessage(Exception ex)
    {
        var message = ex is InvalidOperationException or TenantNotReadyException
            ? ex.Message
            : $"An unexpected error occurred ({ex.GetType().Name}). See the server log for details.";

        return message.Length > MaxErrorLength ? message[..MaxErrorLength] : message;
    }
}

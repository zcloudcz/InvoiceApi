using Fakvio.Contracts.Dto.RecurringInvoice;

namespace Fakvio.Application.Service;

/// <summary>
/// Manages recurring invoice schedules — the configuration that drives automatic invoice
/// generation from a template at a regular cadence (weekly/monthly/quarterly/yearly).
///
/// This is the "stateless service" half of the background-work pattern (DEVGUIDE §6.1):
/// <see cref="RunCycleAsync"/> contains the whole generation cycle for one tenant and is
/// called both by <c>RecurringInvoiceWorker</c> (BackgroundService) and directly by tests —
/// no DB lock, no sleeping, no state kept between calls.
/// </summary>
public interface IRecurringInvoiceService
{
    /// <summary>Gets all schedules for a given invoice template (used by the template detail page).</summary>
    Task<List<RecurringInvoiceScheduleDto>> GetByTemplateAsync(long templateId, CancellationToken ct = default);

    /// <summary>Gets every schedule in the tenant (used by the REST API list endpoint).</summary>
    Task<List<RecurringInvoiceScheduleDto>> GetAllAsync(CancellationToken ct = default);

    /// <summary>Gets a single schedule by ID, or null if it doesn't exist.</summary>
    Task<RecurringInvoiceScheduleDto?> GetByIdAsync(long id, CancellationToken ct = default);

    /// <summary>
    /// Creates a new schedule. Validates the template is active, the client exists, and the
    /// recurrence rule is well-formed (see <c>RecurrenceCalculator</c> validation rules).
    /// Throws <see cref="InvalidOperationException"/> on any validation failure.
    /// </summary>
    Task<RecurringInvoiceScheduleDto> CreateAsync(CreateRecurringInvoiceScheduleDto createDto, CancellationToken ct = default);

    /// <summary>
    /// Updates an existing schedule. Only non-null fields in <paramref name="updateDto"/> are applied.
    /// Throws <see cref="InvalidOperationException"/> if the schedule doesn't exist or the result
    /// would be an invalid recurrence rule.
    /// </summary>
    Task<RecurringInvoiceScheduleDto> UpdateAsync(long id, UpdateRecurringInvoiceScheduleDto updateDto, CancellationToken ct = default);

    /// <summary>Pauses (IsActive = false) or resumes (IsActive = true) a schedule without deleting it.</summary>
    Task<RecurringInvoiceScheduleDto> SetActiveAsync(long id, bool isActive, CancellationToken ct = default);

    /// <summary>
    /// Deletes a schedule. Hard-deletes only when it has never fired
    /// (<c>OccurrenceCount == 0</c>) — otherwise deactivates it, preserving history/audit trail.
    /// </summary>
    Task DeleteAsync(long id, CancellationToken ct = default);

    /// <summary>
    /// Runs one generation cycle for the given tenant: for every active schedule whose
    /// <c>NextRunAt &lt;= nowUtc</c>, generates exactly one invoice (issued as Completed —
    /// see the owner decision in the recurring-invoices story), advances the schedule, and
    /// records success/failure. Called from the per-tenant scope set up by the worker.
    /// </summary>
    /// <returns>Number of invoices successfully generated in this cycle.</returns>
    Task<int> RunCycleAsync(long companyId, DateTimeOffset nowUtc, CancellationToken ct = default);
}

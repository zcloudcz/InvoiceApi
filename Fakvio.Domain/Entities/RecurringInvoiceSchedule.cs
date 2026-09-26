using Fakvio.Domain.Common;
using Fakvio.Domain.Enums;

namespace Fakvio.Domain.Entities;

/// <summary>
/// Defines a recurring invoice schedule — the configuration that drives automatic
/// invoice generation from a template at a regular cadence.
///
/// Lifecycle:
///   1. User creates a schedule pointing at an InvoiceTemplate and a Client.
///   2. The schedule stores the next fire time (<see cref="NextRunAt"/>) and frequency.
///   3. <c>IRecurringInvoiceService.RunCycleAsync</c> (Application layer, separate task)
///      reads all active schedules where <c>NextRunAt &lt;= UtcNow</c>, generates invoices,
///      advances <c>NextRunAt</c> by one period, and writes back <c>LastRunAt</c>.
///   4. Schedule deactivates itself when <see cref="EndDate"/> is reached or when
///      <see cref="MaxOccurrences"/> (if set) has been consumed.
///
/// Multi-replica safety: the service uses a PostgreSQL advisory lock (RecurringInvoiceWorker)
/// to prevent two App Service replicas from double-generating invoices. There is no Azure
/// Function counterpart — since #419/#426 the only host is Fakvio.API (DEVGUIDE §6).
///
/// Inherits <see cref="BaseEntity"/> for Id, CreatedAt/UpdatedAt, and user audit columns.
/// </summary>
public class RecurringInvoiceSchedule : BaseEntity
{
    // ── Template + Client ─────────────────────────────────────────────────────

    /// <summary>
    /// Foreign key to the <see cref="InvoiceTemplate"/> used as the blueprint for
    /// each generated invoice. The template defines line items, currency, issuer, etc.
    /// </summary>
    public long TemplateId { get; set; }

    /// <summary>
    /// Navigation property to the source invoice template.
    /// </summary>
    public InvoiceTemplate Template { get; set; } = null!;

    /// <summary>
    /// Foreign key to the <see cref="Client"/> who will receive each generated invoice.
    /// Stored separately from the template so the same template can drive schedules
    /// for multiple different clients.
    /// </summary>
    public long ClientId { get; set; }

    /// <summary>
    /// Navigation property to the target client (invoice recipient).
    /// </summary>
    public Client Client { get; set; } = null!;

    // ── Recurrence rules ──────────────────────────────────────────────────────

    /// <summary>
    /// How often the invoice repeats (Weekly / Monthly / Quarterly / Yearly).
    /// </summary>
    public ERecurrenceFrequency Frequency { get; set; }

    /// <summary>
    /// Multiplier for the frequency — how many periods between each run.
    /// Default 1 means "every period"; 2 means "every two periods", etc.
    ///
    /// Examples:
    ///   Frequency=Monthly, IntervalCount=1  → monthly
    ///   Frequency=Monthly, IntervalCount=3  → quarterly (equivalent to Quarterly + 1)
    ///   Frequency=Weekly,  IntervalCount=2  → bi-weekly (every 2 weeks)
    /// </summary>
    public int IntervalCount { get; set; } = 1;

    /// <summary>
    /// Day of month on which the invoice fires (1–28).
    /// Capped at 28 to avoid "February 29" edge cases — the service clamps to the
    /// last valid day of the target month when necessary.
    /// Null when Frequency=Weekly (use DayOfWeek instead).
    /// </summary>
    public int? DayOfMonth { get; set; }

    /// <summary>
    /// Day of week on which the invoice fires.
    /// Used when Frequency=Weekly; ignored for Monthly/Quarterly/Yearly.
    /// Null when Frequency != Weekly.
    /// </summary>
    public DayOfWeek? DayOfWeek { get; set; }

    // ── Scheduling state ──────────────────────────────────────────────────────

    /// <summary>
    /// UTC timestamp of the next planned invoice generation.
    /// The recurring job selects all active schedules where NextRunAt &lt;= UtcNow
    /// and generates one invoice per schedule, then advances this field by one period.
    /// </summary>
    public DateTimeOffset NextRunAt { get; set; }

    /// <summary>
    /// UTC timestamp of the most recent successful invoice generation.
    /// Null until the schedule has fired at least once.
    /// </summary>
    public DateTimeOffset? LastRunAt { get; set; }

    /// <summary>
    /// Optional hard stop date — the schedule will not fire on or after this date.
    /// Null means the schedule runs indefinitely (or until MaxOccurrences is reached).
    /// </summary>
    public DateTimeOffset? EndDate { get; set; }

    /// <summary>
    /// Optional cap on how many invoices this schedule may produce in total.
    /// Null means unlimited.
    /// </summary>
    public int? MaxOccurrences { get; set; }

    /// <summary>
    /// Running count of invoices generated so far.
    /// Incremented by the recurring job on each successful generation.
    /// Starts at 0 (never run yet).
    /// </summary>
    public int OccurrenceCount { get; set; } = 0;

    // ── Status flags ──────────────────────────────────────────────────────────

    /// <summary>
    /// Whether this schedule is active and should be considered by the recurring job.
    /// Setting to false pauses the schedule without deleting it.
    /// Default true (schedule is active when created).
    /// </summary>
    public bool IsActive { get; set; } = true;

    /// <summary>
    /// Whether the generated invoice should be automatically sent to the client by email.
    /// When true: service generates invoice, renders PDF, and sends it via EmailService.
    /// When false: invoice is created in Draft status for manual review and sending.
    /// Default false (safer — user reviews before sending).
    /// </summary>
    public bool AutoSend { get; set; } = false;

    // ── Error tracking ────────────────────────────────────────────────────────

    /// <summary>
    /// Message from the last failed generation attempt.
    /// Null when the schedule has never failed or the last run was successful.
    /// Surfaced in the UI so the user can diagnose and fix recurring issues.
    /// </summary>
    public string? LastError { get; set; }

    // ── Optimistic concurrency ────────────────────────────────────────────────

    /// <summary>
    /// PostgreSQL xmin system column used for optimistic concurrency.
    /// Configured via <c>IsConcurrencyToken().ValueGeneratedOnAddOrUpdate()</c> in EF Core —
    /// matches the pattern used by NumberSequence and other high-contention entities.
    /// EF Core throws <see cref="Microsoft.EntityFrameworkCore.DbUpdateConcurrencyException"/>
    /// if another transaction updated this row since we last read it.
    /// </summary>
    public uint RowVersion { get; set; }
}

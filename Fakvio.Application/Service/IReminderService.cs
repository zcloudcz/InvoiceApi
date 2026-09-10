using Fakvio.Contracts.Common.Pagination;
using Fakvio.Contracts.Dto.Reminder;

namespace Fakvio.Application.Service;

/// <summary>
/// Service for managing payment reminders (dunning).
/// Handles settings CRUD, reminder listing, manual actions, and the daily dunning job.
/// Settings use 2-tier resolution: client-level override > company-level default.
/// </summary>
public interface IReminderService
{
    // ─── Settings CRUD ──────────────────────────────────────────────────

    /// <summary>
    /// Get effective settings for a client. Returns client-level override if it exists,
    /// otherwise falls back to the company-wide default.
    /// If clientId is null, returns company default.
    /// </summary>
    Task<ReminderSettingsDto?> GetEffectiveSettingsAsync(long? clientId, CancellationToken ct = default);

    /// <summary>
    /// Get or create company-level default settings.
    /// If no company settings exist yet, creates one with default values.
    /// </summary>
    Task<ReminderSettingsDto> GetCompanySettingsAsync(CancellationToken ct = default);

    /// <summary>
    /// Create or update settings (upsert). If ClientId is null, updates company default.
    /// If ClientId is set, creates/updates a per-client override.
    /// Levels are replaced entirely (delete old + insert new).
    /// </summary>
    Task<ReminderSettingsDto> UpsertSettingsAsync(UpdateReminderSettingsDto dto, CancellationToken ct = default);

    /// <summary>
    /// Delete a client-level override, reverting to company default for that client.
    /// Returns true if an override existed and was deleted, false if not found.
    /// </summary>
    Task<bool> DeleteClientSettingsAsync(long clientId, CancellationToken ct = default);

    /// <summary>
    /// List all client-level overrides (for the settings management page).
    /// </summary>
    Task<List<ReminderSettingsDto>> GetClientOverridesAsync(CancellationToken ct = default);

    // ─── Reminder CRUD + actions ────────────────────────────────────────

    /// <summary>
    /// List reminders with pagination and filtering (by client, invoice, status, date range).
    /// </summary>
    Task<PagedResult<ReminderDto>> GetRemindersPagedAsync(ReminderFilterDto filter, CancellationToken ct = default);

    /// <summary>
    /// List all reminders for a specific invoice, ordered by level.
    /// </summary>
    Task<List<ReminderDto>> GetByInvoiceAsync(long invoiceId, CancellationToken ct = default);

    /// <summary>
    /// Get a single reminder by ID.
    /// </summary>
    Task<ReminderDto?> GetByIdAsync(long reminderId, CancellationToken ct = default);

    /// <summary>
    /// Manually send a Draft reminder (resolves template, sends email, updates status).
    /// Throws if the reminder is not in Draft status.
    /// </summary>
    Task<ReminderDto> SendReminderAsync(long reminderId, CancellationToken ct = default);

    /// <summary>
    /// Cancel a reminder (sets Status = Cancelled with optional notes).
    /// Used when an invoice is paid, disputed, or credit-noted.
    /// </summary>
    Task<ReminderDto> CancelReminderAsync(long reminderId, string? notes = null, CancellationToken ct = default);

    // ─── Dunning job ────────────────────────────────────────────────────

    /// <summary>
    /// Process all overdue invoices for the current tenant.
    /// Creates reminders and optionally sends emails based on settings.
    /// Called by the daily <c>ReminderWorker</c> (BackgroundService, 06:00 UTC).
    /// Returns the number of reminders created.
    /// </summary>
    Task<int> ProcessOverdueInvoicesAsync(CancellationToken ct = default);

    // ─── Dashboard / statistics ─────────────────────────────────────────

    /// <summary>
    /// Get summary statistics for the dashboard widget.
    /// Returns counts of reminders by status and totals.
    /// </summary>
    Task<ReminderDashboardDto> GetDashboardDataAsync(CancellationToken ct = default);
}

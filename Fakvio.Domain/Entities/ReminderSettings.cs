using Fakvio.Domain.Common;

namespace Fakvio.Domain.Entities;

/// <summary>
/// Configuration for automatic payment reminders (dunning).
/// Exists at two levels:
///   1. Company-level default (ClientId = null) — applies to all clients unless overridden.
///   2. Client-level override (ClientId = non-null) — takes precedence for that specific client.
///
/// Each tenant has exactly one company-level record. Per-client overrides are optional.
/// The dunning job resolves effective settings by: client override > company default.
/// </summary>
public class ReminderSettings : BaseEntity
{
    /// <summary>
    /// FK to Client. Null = company-wide default settings.
    /// Non-null = per-client override (this client uses custom reminder rules).
    /// </summary>
    public long? ClientId { get; set; }

    /// <summary>
    /// Navigation property to Client (null for company default).
    /// </summary>
    public Client? Client { get; set; }

    /// <summary>
    /// Master switch — when false, no reminders are generated for this scope.
    /// If company default is disabled, no reminders are sent unless a client override enables them.
    /// </summary>
    public bool IsEnabled { get; set; } = true;

    /// <summary>
    /// Maximum number of escalation levels (1–5).
    /// After reaching this level, no further reminders are created for the invoice.
    /// Default: 3 (friendly reminder → formal notice → final warning).
    /// </summary>
    public int MaxReminderLevel { get; set; } = 3;

    /// <summary>
    /// Days to wait after the invoice due date before sending the first reminder.
    /// Gives the client a grace period to pay before the dunning process starts.
    /// Default: 7 days.
    /// </summary>
    public int GracePeriodDays { get; set; } = 7;

    /// <summary>
    /// Whether to calculate and include statutory late payment interest (§ 1970 OZ).
    /// Interest = principal × (CNB repo rate + 8 p.p.) / 100 × days / 365.
    /// Default: false (informational only, not automatically billed).
    /// </summary>
    public bool IncludeInterest { get; set; } = false;

    /// <summary>
    /// Whether to attach the original invoice PDF to the reminder email.
    /// Helps the client identify which invoice is overdue without searching their records.
    /// Default: true.
    /// </summary>
    public bool AttachInvoicePdf { get; set; } = true;

    /// <summary>
    /// Whether to automatically send the email when a reminder is created by the daily job.
    /// When false, reminders are created as Draft for manual review and sending.
    /// Default: true.
    /// </summary>
    public bool AutoSendEmail { get; set; } = true;

    /// <summary>
    /// Escalation levels defining timing, fees, and templates for each reminder step.
    /// Ordered by Level (1, 2, 3, ...).
    /// </summary>
    public ICollection<ReminderLevel> Levels { get; set; } = new List<ReminderLevel>();
}

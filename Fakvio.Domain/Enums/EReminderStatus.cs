namespace Fakvio.Domain.Enums;

/// <summary>
/// Status of a payment reminder (dunning letter).
/// Tracks the lifecycle of each reminder record from creation to resolution.
/// </summary>
public enum EReminderStatus
{
    /// <summary>
    /// Reminder has been created but not yet sent.
    /// Waiting for manual approval or scheduled sending.
    /// </summary>
    Draft = 0,

    /// <summary>
    /// Reminder email has been successfully sent to the client.
    /// SentAt and SentToEmail fields are populated.
    /// </summary>
    Sent = 1,

    /// <summary>
    /// Email sending failed (SMTP error, invalid email, etc.).
    /// ErrorMessage field contains the failure reason.
    /// </summary>
    Failed = 2,

    /// <summary>
    /// Reminder was manually cancelled by the user.
    /// Common reasons: invoice paid in the meantime, dispute resolved, credit note issued.
    /// </summary>
    Cancelled = 3
}

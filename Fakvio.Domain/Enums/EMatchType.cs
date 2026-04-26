namespace Fakvio.Domain.Enums;

/// <summary>
/// How a PaymentMatch row was created.
/// Useful for audit — which matches were automatic vs. human-approved.
/// </summary>
public enum EMatchType
{
    /// <summary>Created by the auto-matcher (PaymentMatchingService).</summary>
    Auto = 1,

    /// <summary>Created by a user action in the UI ("Match with invoice…").</summary>
    Manual = 2,

    /// <summary>Suggestion only — not yet confirmed by user. Reserved for future workflow.</summary>
    Suggested = 3
}

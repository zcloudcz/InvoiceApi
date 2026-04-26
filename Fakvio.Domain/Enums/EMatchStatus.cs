namespace Fakvio.Domain.Enums;

/// <summary>
/// Whether a BankTransaction has been successfully linked to an invoice.
/// Drives the filter tabs on the Payments grid.
/// </summary>
public enum EMatchStatus
{
    /// <summary>No link yet. User needs to review and match manually (or matcher will retry).</summary>
    Unmatched = 1,

    /// <summary>Fully linked to one or more invoices; their sum equals this transaction.</summary>
    Matched = 2,

    /// <summary>Linked to invoice(s) but transaction amount exceeds remaining — overpayment left over.</summary>
    PartiallyMatched = 3,

    /// <summary>User explicitly marked this as unrelated (e.g., personal withdrawal, ATM fee).</summary>
    Ignored = 4,

    /// <summary>Multiple candidates detected or ambiguous data — user must decide.</summary>
    NeedsReview = 5
}

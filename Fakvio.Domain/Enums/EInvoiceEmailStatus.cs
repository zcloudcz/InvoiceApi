namespace Fakvio.Domain.Enums;

/// <summary>
/// Processing status of an inbound invoice email.
/// </summary>
public enum EInvoiceEmailStatus
{
    /// <summary>Email archived, awaiting processing.</summary>
    Pending = 1,

    /// <summary>Successfully parsed and invoice/received invoice created.</summary>
    Imported = 2,

    /// <summary>Low confidence — imported but flagged for user review.</summary>
    NeedsReview = 3,

    /// <summary>Parser or classification failed after retries.</summary>
    Failed = 4,

    /// <summary>User marked as not-an-invoice (skip).</summary>
    Ignored = 5
}

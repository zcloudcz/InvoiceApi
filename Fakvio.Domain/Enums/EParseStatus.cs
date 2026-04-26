namespace Fakvio.Domain.Enums;

/// <summary>
/// Parsing status of an archived InboundEmail.
/// An email moves Pending → Parsed (happy path) or Pending → NeedsReview / Failed (otherwise).
/// </summary>
public enum EParseStatus
{
    /// <summary>Email archived, waiting for the parser worker to pick it up.</summary>
    Pending = 1,

    /// <summary>AI parsed it successfully; BankTransactionId is populated.</summary>
    Parsed = 2,

    /// <summary>AI parsed it but confidence too low or ambiguous — user must review.</summary>
    NeedsReview = 3,

    /// <summary>AI parser failed after all retries. ParseError has the last exception message.</summary>
    Failed = 4,

    /// <summary>User explicitly marked this email as not-a-payment (marketing, login alert, …).</summary>
    Ignored = 5
}

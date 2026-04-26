namespace Fakvio.Domain.Enums;

/// <summary>
/// How a BankTransaction entered the system.
/// Used for audit, filtering, and deciding whether to re-parse.
/// </summary>
public enum EImportSource
{
    /// <summary>Parsed from an email notification pulled via IMAP (primary source in MVP).</summary>
    InboundEmail = 1,

    /// <summary>Polled from Fio Bank REST API (phase 2).</summary>
    FioApi = 2,

    /// <summary>User uploaded a GPC (ABO-K) bank statement file (phase 3).</summary>
    GpcImport = 3,

    /// <summary>User uploaded a CAMT.053 ISO 20022 XML bank statement (phase 3).</summary>
    CamtImport = 4,

    /// <summary>Virtual transaction created by user clicking "Mark as Paid" — kept for unified audit.</summary>
    Manual = 5
}

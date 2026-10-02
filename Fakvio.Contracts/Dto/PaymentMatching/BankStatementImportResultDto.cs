namespace Fakvio.Contracts.Dto.PaymentMatching;

/// <summary>Summary returned by POST api/bank-statement/import (GPC/ABO bank statement import).</summary>
public class BankStatementImportResultDto
{
    /// <summary>Number of statements (074 records) found in the file.</summary>
    public int Statements { get; set; }

    /// <summary>New bank transactions created.</summary>
    public int Imported { get; set; }

    /// <summary>Items skipped because the same transaction was already imported earlier.</summary>
    public int Duplicates { get; set; }

    /// <summary>Imported incoming payments that the matcher paired with an invoice.</summary>
    public int Matched { get; set; }

    /// <summary>Imported incoming payments left unmatched for manual review.</summary>
    public int Unmatched { get; set; }

    /// <summary>Malformed lines, storno items and statements whose account could not be resolved (human-readable, English).</summary>
    public List<string> Errors { get; set; } = new();
}

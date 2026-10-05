namespace Fakvio.Contracts.Dto.PaymentMatching;

/// <summary>Summary returned by POST api/bank-statement/import (GPC/ABO bank statement import).</summary>
public class BankStatementImportResultDto
{
    /// <summary>Number of statements (074 records) found in the file.</summary>
    public int Statements { get; set; }

    /// <summary>New bank transactions created.</summary>
    public int Imported { get; set; }

    /// <summary>Items skipped because the same transaction was already imported (earlier GPC import or IMAP e-mail).</summary>
    public int Duplicates { get; set; }

    /// <summary>Imported payments (incoming and outgoing) that the matcher paired with an issued/received invoice.</summary>
    public int Matched { get; set; }

    /// <summary>Imported payments (incoming and outgoing) left unmatched or needing review.</summary>
    public int Unmatched { get; set; }

    /// <summary>Malformed lines, storno items and statements whose account could not be resolved (human-readable, English).</summary>
    public List<string> Errors { get; set; } = new();
}

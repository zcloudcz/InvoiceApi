using Fakvio.Domain.Enums;

namespace Fakvio.Contracts.Dto.PaymentMatching;

/// <summary>
/// Grid-friendly projection of a BankTransaction. Flattened navigation-derived
/// fields (BankAccountLabel, InvoiceNumbers) so the Blazor grid doesn't have to
/// navigate further.
/// </summary>
public class BankTransactionDto
{
    public long Id { get; set; }
    public long BankAccountId { get; set; }
    public string? BankAccountLabel { get; set; }

    public DateTime TransactionDate { get; set; }
    public decimal Amount { get; set; }
    public string CurrencyCode { get; set; } = "CZK";
    public EPaymentDirection Direction { get; set; }

    public string? VariableSymbol { get; set; }
    public string? ConstantSymbol { get; set; }
    public string? SpecificSymbol { get; set; }
    public string? CounterpartyAccount { get; set; }
    public string? CounterpartyName { get; set; }
    public string? Message { get; set; }

    public EImportSource ImportSource { get; set; }
    public EMatchStatus MatchStatus { get; set; }

    public decimal? ParserConfidence { get; set; }
    public string? ParserModel { get; set; }

    /// <summary>Document numbers of invoices this transaction has been matched to.</summary>
    public List<string> MatchedInvoiceNumbers { get; set; } = new();

    /// <summary>Sum of MatchedAmount across all PaymentMatch rows for this transaction.</summary>
    public decimal MatchedTotal { get; set; }

    /// <summary>FK to the recognized counterparty registry entry (MatchStatus = Recognized).</summary>
    public long? RecognizedCounterpartyId { get; set; }

    /// <summary>Label of the recognized counterparty ("OSSZ — sociální pojištění").</summary>
    public string? RecognizedCounterpartyLabel { get; set; }

    /// <summary>Payment category of the recognized counterparty (for reporting).</summary>
    public EPaymentCategory? RecognizedCategory { get; set; }
}

/// <summary>Filter parameters used by the Payments grid.</summary>
public class BankTransactionFilterDto
{
    public EMatchStatus? Status { get; set; }
    public EPaymentDirection? Direction { get; set; }
    public long? BankAccountId { get; set; }
    public DateTime? From { get; set; }
    public DateTime? To { get; set; }
    public string? Search { get; set; } // free-text over counterparty/message/VS
}

/// <summary>Payload for the manual-match dialog.</summary>
public class ManualMatchRequest
{
    public long InvoiceId { get; set; }
    public decimal Amount { get; set; }
    public string? Note { get; set; }
}

/// <summary>Payload for Unmatch.</summary>
public class UnmatchRequest
{
    public long PaymentMatchId { get; set; }
    public string? Reason { get; set; }
}

/// <summary>Response to a successful manual-match call.</summary>
public class ManualMatchResponse
{
    public long PaymentMatchId { get; set; }
    public decimal InvoicePaidAmount { get; set; }
    public decimal InvoiceRemaining { get; set; }
}

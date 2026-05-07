namespace Fakvio.Contracts.Dto.PaymentMatching;

/// <summary>
/// Proposal returned by FindAutoMatchFor*Async — describes the candidate bank transaction
/// found by the auto-matcher. The user sees this in the confirmation dialog and can
/// accept or reject the match.
/// </summary>
public class AutoMatchProposalDto
{
    /// <summary>Id of the candidate BankTransaction.</summary>
    public long BankTransactionId { get; set; }

    /// <summary>Date the transaction settled at the bank.</summary>
    public DateTime TransactionDate { get; set; }

    /// <summary>Amount of the bank transaction.</summary>
    public decimal Amount { get; set; }

    /// <summary>Currency ISO code (e.g. "CZK").</summary>
    public string CurrencyCode { get; set; } = "CZK";

    /// <summary>Name of the counterparty (sender or recipient).</summary>
    public string? CounterpartyName { get; set; }

    /// <summary>Variable symbol on the bank transaction (may be null).</summary>
    public string? VariableSymbol { get; set; }

    /// <summary>Free-text message / payment reference from the bank.</summary>
    public string? Message { get; set; }
}

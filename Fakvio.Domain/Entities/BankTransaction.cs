using Fakvio.Domain.Common;
using Fakvio.Domain.Enums;

namespace Fakvio.Domain.Entities;

/// <summary>
/// One parsed bank transaction (credit or debit) on a specific BankAccount.
/// Created either by the AI email parser, by a future bank API integration,
/// by a file import, or virtually by manual "Mark as Paid".
///
/// Matching: the PaymentMatchingService creates PaymentMatch rows that link
/// this transaction to one or more invoices. A single transaction can cover
/// multiple invoices (bulk payment) and a single invoice can be covered by
/// multiple transactions (partial payments).
/// </summary>
public class BankTransaction : BaseEntity
{
    /// <summary>FK to the bank account on which this transaction happened.</summary>
    public long BankAccountId { get; set; }

    /// <summary>Navigation to the bank account.</summary>
    public BankAccount BankAccount { get; set; } = null!;

    /// <summary>
    /// Idempotency key — SHA-256 over stable identifying fields so the same
    /// transaction re-delivered (IMAP reconnect, duplicate email) is not
    /// inserted twice. Unique per (BankAccountId).
    /// </summary>
    public string DeduplicationHash { get; set; } = string.Empty;

    /// <summary>Date/time the bank processed the transaction (from the email or API payload).</summary>
    public DateTime TransactionDate { get; set; }

    /// <summary>Transaction amount in the transaction currency. Always positive; Direction tells sign.</summary>
    public decimal Amount { get; set; }

    /// <summary>ISO 4217 currency code (e.g., "CZK", "EUR").</summary>
    public string CurrencyCode { get; set; } = "CZK";

    /// <summary>Incoming (credit) or outgoing (debit) relative to account owner.</summary>
    public EPaymentDirection Direction { get; set; }

    /// <summary>Variable symbol extracted from the email/payload. Digits only, max 10 chars.</summary>
    public string? VariableSymbol { get; set; }

    /// <summary>Constant symbol — payment category. Digits only, max 4 chars.</summary>
    public string? ConstantSymbol { get; set; }

    /// <summary>Specific symbol — optional identifier. Digits only, max 10 chars.</summary>
    public string? SpecificSymbol { get; set; }

    /// <summary>Counterparty account (payer for incoming, payee for outgoing). Czech or IBAN format.</summary>
    public string? CounterpartyAccount { get; set; }

    /// <summary>Counterparty human-readable name if the bank provided one.</summary>
    public string? CounterpartyName { get; set; }

    /// <summary>"Zpráva pro příjemce" — free-text message.</summary>
    public string? Message { get; set; }

    /// <summary>
    /// Bank's transaction identifier ("Kód transakce") when present in the
    /// notification — the only unique identifier of CARD payments, which carry
    /// no VS and no counterparty account. Included in DeduplicationHash so two
    /// same-day card payments of the same amount are not collapsed as duplicates.
    /// </summary>
    public string? TransactionCode { get; set; }

    /// <summary>How this row got into the system — email, Fio API, import, manual.</summary>
    public EImportSource ImportSource { get; set; }

    /// <summary>Raw payload (truncated to 64 KB) for debugging and reparse. JSON for API sources.</summary>
    public string? RawPayload { get; set; }

    /// <summary>AI parser confidence 0..1 (null for structured sources like Fio API).</summary>
    public decimal? ParserConfidence { get; set; }

    /// <summary>Model identifier that produced the parse (e.g., "claude-haiku-4-5-20251001").</summary>
    public string? ParserModel { get; set; }

    /// <summary>Current state of matching against invoices.</summary>
    public EMatchStatus MatchStatus { get; set; } = EMatchStatus.Unmatched;

    /// <summary>
    /// FK to the recognized counterparty registry entry this transaction was
    /// assigned to (when MatchStatus = Recognized). Null otherwise.
    /// SetNull on registry-entry delete — the service also resets MatchStatus.
    /// </summary>
    public long? RecognizedCounterpartyId { get; set; }

    /// <summary>Navigation to the recognized counterparty registry entry.</summary>
    public RecognizedCounterparty? RecognizedCounterparty { get; set; }

    /// <summary>Matches created for this transaction.</summary>
    public ICollection<PaymentMatch> PaymentMatch { get; set; } = new List<PaymentMatch>();
}

using Fakvio.Domain.Common;
using Fakvio.Domain.Enums;

namespace Fakvio.Domain.Entities;

/// <summary>
/// Tenant-level registry of known counterparty bank accounts used to recognize
/// recurring payments that have no invoice — social insurance (ČSSZ), health
/// insurance, sickness insurance, VAT / income tax to the tax office (FÚ), etc.
///
/// When the payment matcher fails to match a BankTransaction to any invoice,
/// it falls back to this registry: if the transaction's counterparty account
/// (normalized) equals <see cref="CounterpartyAccount"/> and every symbol
/// filled on this entry equals the transaction's symbol, the transaction is
/// marked EMatchStatus.Recognized and linked via BankTransaction.RecognizedCounterpartyId.
///
/// Works in both directions — e.g. a VAT refund arrives as an INCOMING payment
/// from the same tax office account.
/// </summary>
public class RecognizedCounterparty : BaseEntity
{
    /// <summary>
    /// Display name shown on the Payments grid when a transaction is recognized.
    /// Example: "OSSZ — sociální pojištění", "VZP — zdravotní pojištění", "FÚ — DPH".
    /// </summary>
    public string Label { get; set; } = string.Empty;

    /// <summary>
    /// Counterparty bank account number as entered by the user (Czech format
    /// "number/bankcode" or IBAN). Stored raw; matching compares a normalized
    /// form (whitespace/hyphens stripped, uppercased) on both sides.
    /// Optional since card-payment support — at least one of
    /// (CounterpartyAccount, CounterpartyNamePattern) must be filled
    /// (enforced by RecognizedCounterpartyService validation).
    /// </summary>
    public string? CounterpartyAccount { get; set; }

    /// <summary>
    /// Optional merchant/counterparty name pattern for payments that carry no
    /// account number — typically CARD payments ("Platba kartou v ANTHROPIC*
    /// CLAUDE SUB, SAN FRANCISCO, CA"). Matched as a case-insensitive substring
    /// against the transaction's CounterpartyName AND Message.
    /// </summary>
    public string? CounterpartyNamePattern { get; set; }

    /// <summary>
    /// Optional variable symbol constraint. Null/empty = wildcard (any VS matches).
    /// Useful when one nationwide account (e.g. tax office) serves many payers —
    /// the VS is typically the payer's identifier (DIČ/rodné číslo derived).
    /// </summary>
    public string? VariableSymbol { get; set; }

    /// <summary>
    /// Optional specific symbol constraint. Null/empty = wildcard.
    /// </summary>
    public string? SpecificSymbol { get; set; }

    /// <summary>
    /// Optional constant symbol constraint. Null/empty = wildcard.
    /// KS is already parsed on transactions (e.g. "7618" for insurance payments).
    /// </summary>
    public string? ConstantSymbol { get; set; }

    /// <summary>
    /// Optional payment category for future reporting (expense breakdown by type).
    /// Null = uncategorized; the Label alone is enough for recognition.
    /// </summary>
    public EPaymentCategory? Category { get; set; }

    /// <summary>
    /// Optional free-text note (e.g. payment schedule, contact, reference).
    /// </summary>
    public string? Note { get; set; }

    /// <summary>
    /// Soft-disable: inactive entries are kept for history but skipped by the
    /// matcher and hidden from pickers.
    /// </summary>
    public bool IsActive { get; set; } = true;
}

using Fakvio.Domain.Enums;

namespace Fakvio.Contracts.Dto.RecognizedCounterparty;

/// <summary>
/// Registry entry of a known counterparty bank account (insurance, tax office, …)
/// used to recognize recurring payments that have no invoice.
/// </summary>
public class RecognizedCounterpartyDto
{
    public long Id { get; set; }

    /// <summary>Display name shown on the Payments grid ("OSSZ — sociální pojištění").</summary>
    public string Label { get; set; } = string.Empty;

    /// <summary>Counterparty account as entered (Czech "number/bankcode" or IBAN).</summary>
    public string CounterpartyAccount { get; set; } = string.Empty;

    /// <summary>Optional variable symbol constraint. Null/empty = any VS matches.</summary>
    public string? VariableSymbol { get; set; }

    /// <summary>Optional specific symbol constraint. Null/empty = any SS matches.</summary>
    public string? SpecificSymbol { get; set; }

    /// <summary>Optional constant symbol constraint. Null/empty = any KS matches.</summary>
    public string? ConstantSymbol { get; set; }

    /// <summary>Optional payment category for reporting.</summary>
    public EPaymentCategory? Category { get; set; }

    /// <summary>Optional free-text note.</summary>
    public string? Note { get; set; }

    /// <summary>Inactive entries are skipped by the matcher and hidden from pickers.</summary>
    public bool IsActive { get; set; } = true;
}

/// <summary>
/// Create/update payload for a registry entry (same shape minus Id).
/// </summary>
public class SaveRecognizedCounterpartyRequest
{
    public string Label { get; set; } = string.Empty;
    public string CounterpartyAccount { get; set; } = string.Empty;
    public string? VariableSymbol { get; set; }
    public string? SpecificSymbol { get; set; }
    public string? ConstantSymbol { get; set; }
    public EPaymentCategory? Category { get; set; }
    public string? Note { get; set; }
    public bool IsActive { get; set; } = true;
}

/// <summary>
/// Result of create/update — includes how many previously unmatched transactions
/// were recognized by the automatic re-scan (shown in the UI snackbar).
/// </summary>
public class SaveRecognizedCounterpartyResponse
{
    public RecognizedCounterpartyDto Entry { get; set; } = null!;

    /// <summary>Number of transactions newly recognized by the rescan after this save.</summary>
    public int RecognizedCount { get; set; }
}

/// <summary>Payload for manually assigning a transaction to a registry entry.</summary>
public class AssignRecognizedRequest
{
    public long RecognizedCounterpartyId { get; set; }
}

namespace Fakvio.Domain.Enums;

/// <summary>
/// Direction of a bank transaction relative to the account owner.
/// Junior note: From OUR company's point of view:
///   - Incoming: someone paid us (money came IN) → match with issued invoices
///   - Outgoing: we paid someone (money went OUT) → match with received invoices
/// </summary>
public enum EPaymentDirection
{
    /// <summary>Money received from a payer (credit). Matches issued invoices.</summary>
    Incoming = 1,

    /// <summary>Money sent to a payee (debit). Matches received invoices.</summary>
    Outgoing = 2
}

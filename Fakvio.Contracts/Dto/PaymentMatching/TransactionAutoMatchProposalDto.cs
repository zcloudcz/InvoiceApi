namespace Fakvio.Contracts.Dto.PaymentMatching;

/// <summary>
/// Proposal returned by FindAutoMatchForTransactionAsync — describes the best candidate
/// Invoice found for a given unmatched bank transaction.
/// The user sees this in the confirmation dialog and can accept or reject the match.
/// This is the "reverse" of AutoMatchProposalDto — instead of finding a transaction for
/// an invoice, we find an invoice for a transaction.
/// </summary>
public class TransactionAutoMatchProposalDto
{
    /// <summary>Id of the candidate Invoice (issued invoice). Null when the match is a ReceivedInvoice.</summary>
    public long? InvoiceId { get; set; }

    /// <summary>Id of the candidate ReceivedInvoice. Null when the match is an issued Invoice.</summary>
    public long? ReceivedInvoiceId { get; set; }

    /// <summary>True when the proposal points to a ReceivedInvoice, false for an issued Invoice.</summary>
    public bool IsReceivedInvoice => ReceivedInvoiceId.HasValue;

    /// <summary>Human-readable document number, e.g. "FAK-2025-0042".</summary>
    public string DocumentNumber { get; set; } = string.Empty;

    /// <summary>Name of the client the invoice was issued to.</summary>
    public string? ClientName { get; set; }

    /// <summary>Total amount including VAT.</summary>
    public decimal TotalWithVat { get; set; }

    /// <summary>How much has been paid so far (may be partial).</summary>
    public decimal PaidAmount { get; set; }

    /// <summary>Remaining amount to be paid (TotalWithVat - PaidAmount).</summary>
    public decimal Remaining { get; set; }

    /// <summary>Currency ISO code (e.g. "CZK").</summary>
    public string CurrencyCode { get; set; } = "CZK";

    /// <summary>Invoice due date.</summary>
    public DateTime? DueDate { get; set; }

    /// <summary>Variable symbol on the invoice (matched against the transaction's VS).</summary>
    public string? VariableSymbol { get; set; }
}

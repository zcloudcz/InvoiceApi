using System.ComponentModel.DataAnnotations;
using Fakvio.Domain.Enums;

namespace Fakvio.Contracts.Dto.ReceivedInvoice;

/// <summary>
/// DTO for updating a received invoice.
/// All fields are optional — only provided fields are updated.
/// </summary>
public class UpdateReceivedInvoiceDto
{
    [StringLength(100)]
    public string? DocumentNumber { get; set; }

    public DateTime? IssueDate { get; set; }
    public DateTime? ReceivedDate { get; set; }
    public DateTime? DueDate { get; set; }
    public DateTime? TaxableSupplyDate { get; set; }

    [StringLength(50)]
    public string? VariableSymbol { get; set; }

    public long? CurrencyId { get; set; }

    /// <summary>
    /// Manual CZK-per-one-unit exchange rate for a non-CZK document (optional; must be &gt; 0).
    /// Normally omitted — the ČNB rate for the DUZP is assigned when the document is issued/approved.
    /// Allowed only while the document is still a draft / not yet approved.
    /// </summary>
    [Range(typeof(decimal), "0.00000001", "1000000")]
    public decimal? ExchangeRate { get; set; }
    public EPaymentMethod? PaymentMethod { get; set; }

    [StringLength(100)]
    public string? BankAccountNumber { get; set; }

    [StringLength(50)]
    public string? IBAN { get; set; }

    [StringLength(50)]
    public string? SWIFT { get; set; }

    [StringLength(5000)]
    public string? Notes { get; set; }

    /// <summary>
    /// If provided, replaces all existing items.
    /// </summary>
    public List<CreateReceivedInvoiceItemDto>? Items { get; set; }
}

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

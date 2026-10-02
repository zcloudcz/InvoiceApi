using System.ComponentModel.DataAnnotations;
using Fakvio.Domain.Enums;

namespace Fakvio.Contracts.Dto.Invoice;

/// <summary>
/// DTO for updating an existing invoice or credit note
/// All fields are optional - only provided fields will be updated
/// </summary>
public class UpdateInvoiceDto
{
    /// <summary>
    /// Opt-in/out of the EU OSS regime (see CreateInvoiceDto.ApplyOss). Null = keep the invoice's current regime.
    /// </summary>
    public bool? ApplyOss { get; set; }

    /// <summary>
    /// Issue date - when the invoice was issued
    /// </summary>
    public DateTime? IssueDate { get; set; }

    /// <summary>
    /// Due date - when payment is due
    /// </summary>
    public DateTime? DueDate { get; set; }

    /// <summary>
    /// Date of taxable supply (DUZP)
    /// </summary>
    public DateTime? TaxableSupplyDate { get; set; }

    /// <summary>
    /// Variable symbol for payment — Czech banking requires max 10 digits only.
    /// </summary>
    [StringLength(10)]
    [RegularExpression(@"^\d{0,10}$", ErrorMessage = "Variable symbol must contain only digits (max 10)")]
    public string? VariableSymbol { get; set; }

    /// <summary>
    /// Constant symbol for payment
    /// </summary>
    [StringLength(50)]
    public string? ConstantSymbol { get; set; }

    /// <summary>
    /// Specific symbol for payment
    /// </summary>
    [StringLength(50)]
    public string? SpecificSymbol { get; set; }

    /// <summary>
    /// Optional reference to one of the issuer's bank accounts (BankAccount.Id).
    /// When set, the server loads that account and copies its AccountNumber/IBAN/SWIFT onto the
    /// invoice, overriding any explicit BankAccountNumber/IBAN/SWIFT also sent below. The account
    /// must belong to the invoice's issuer — otherwise the request fails with 400.
    /// </summary>
    public long? BankAccountId { get; set; }

    /// <summary>
    /// Bank account number
    /// </summary>
    [StringLength(100)]
    public string? BankAccountNumber { get; set; }

    /// <summary>
    /// IBAN for international payments
    /// </summary>
    [StringLength(50)]
    public string? IBAN { get; set; }

    /// <summary>
    /// SWIFT/BIC code
    /// </summary>
    [StringLength(50)]
    public string? SWIFT { get; set; }

    /// <summary>
    /// Payment method — system enum for consistency
    /// </summary>
    public EPaymentMethod? PaymentMethod { get; set; }

    /// <summary>
    /// Currency ID (foreign key to Currency table)
    /// </summary>
    public long? CurrencyId { get; set; }

    /// <summary>
    /// Optional notes on the invoice
    /// </summary>
    [StringLength(5000)]
    public string? Notes { get; set; }

    /// <summary>
    /// Updated invoice line items
    /// If provided, replaces all existing items
    /// </summary>
    public List<CreateInvoiceItemDto>? InvoiceItem { get; set; }
}

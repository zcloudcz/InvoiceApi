using System.ComponentModel.DataAnnotations;
using Fakvio.Domain.Enums;

namespace Fakvio.Contracts.Dto.Invoice;

/// <summary>
/// DTO for creating a new invoice or credit note
/// </summary>
public class CreateInvoiceDto
{
    /// <summary>
    /// Opt-in to the EU OSS (One-Stop-Shop) regime for this invoice (DEVGUIDE §4.16). Honoured only when the
    /// invoice is eligible (OSS-registered VAT-payer issuer, consumer client in another EU state); asking for it
    /// on an ineligible invoice is rejected. Default false: general B2C services are taxed in CZ (Art. 45),
    /// OSS applies to goods distance sales, telecom/broadcasting/electronic services, etc. — only the user knows.
    /// Ignored for credit notes (they inherit the original invoice's regime).
    /// </summary>
    public bool ApplyOss { get; set; }

    /// <summary>
    /// Type of document - Invoice or CreditNote
    /// </summary>
    [Required]
    public EDocumentType DocumentType { get; set; }

    /// <summary>
    /// Client ID (who receives the invoice)
    /// </summary>
    [Required]
    public long ClientId { get; set; }

    /// <summary>
    /// Issuer ID (who issues the invoice)
    /// Usually your company - should be a client with IsIssuer = true
    /// </summary>
    [Required]
    public long IssuerId { get; set; }

    /// <summary>
    /// Issue date - when the invoice is created
    /// If not provided, today's date is used
    /// </summary>
    public DateTime? IssueDate { get; set; }

    /// <summary>
    /// Due date - when payment is due
    /// If not provided, calculated based on client's billing settings
    /// </summary>
    public DateTime? DueDate { get; set; }

    /// <summary>
    /// Date of taxable supply (DUZP)
    /// Required for VAT payers
    /// If not provided, same as IssueDate
    /// </summary>
    public DateTime? TaxableSupplyDate { get; set; }

    /// <summary>
    /// For credit notes: reference to original invoice
    /// Required if DocumentType = CreditNote
    /// </summary>
    public long? OriginalInvoiceId { get; set; }

    /// <summary>
    /// Variable symbol for payment — Czech banking requires max 10 digits only.
    /// If not provided, auto-generated from document number (digits only, truncated to 10).
    /// </summary>
    [StringLength(10)]
    [RegularExpression(@"^\d{0,10}$", ErrorMessage = "Variable symbol must contain only digits (max 10)")]
    public string? VariableSymbol { get; set; }

    /// <summary>
    /// When false (default), the backend always re-derives VariableSymbol from the
    /// generated DocumentNumber (digits only, max 10). This ensures VS stays in sync
    /// with the DocumentNumber even when the UI sent a preview value without prefix/suffix.
    ///
    /// Set to true only when the user has deliberately typed a custom VS that differs
    /// from the DocumentNumber — e.g. to match a purchase order number.
    ///
    /// UI note: the create-from-template and standard create flows always leave this
    /// false so the backend guard applies. The user must explicitly opt-in to override.
    /// </summary>
    public bool VariableSymbolIsManualOverride { get; set; } = false;

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
    /// must belong to the issuer (IssuerId) — otherwise the request fails with 400.
    /// When omitted and no bank fields are given at all, the server auto-fills from the issuer's
    /// default/currency-matching account (see InvoiceService.ApplyBankAccountDefaultsAsync).
    /// </summary>
    public long? BankAccountId { get; set; }

    /// <summary>
    /// Bank account number
    /// If not provided, taken from the issuer's default bank account (see BankAccountId)
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
    /// Payment method — system enum for consistency and localized display
    /// </summary>
    public EPaymentMethod? PaymentMethod { get; set; }

    /// <summary>
    /// Currency ID (foreign key to Currency table)
    /// </summary>
    [Required]
    public long CurrencyId { get; set; }

    /// <summary>
    /// Manual CZK-per-one-unit exchange rate for a non-CZK document (optional; must be &gt; 0).
    /// Normally omitted — the ČNB rate for the DUZP is assigned when the document is issued/approved.
    /// Allowed only while the document is still a draft / not yet approved.
    /// </summary>
    // Numeric (double) overload on purpose: the MCP schema generator copies Range arguments into
    // JSON Schema minimum/maximum verbatim, so the string overload emitted "0.00000001" as a string —
    // invalid JSON Schema that made ChatGPT reject the whole tools/list. Validation is unchanged.
    [Range(0.00000001, 1000000)]
    public decimal? ExchangeRate { get; set; }

    /// <summary>
    /// Optional notes on the invoice
    /// </summary>
    [StringLength(5000)]
    public string? Notes { get; set; }

    /// <summary>
    /// Custom document number
    /// If not provided, generated automatically from number sequence
    /// Only allowed in Draft status
    /// </summary>
    [StringLength(100)]
    public string? CustomDocumentNumber { get; set; }

    /// <summary>
    /// Optional number sequence override — when set, uses this specific sequence
    /// instead of the issuer's default. Typically set when creating from a template
    /// that has a custom NumberSequenceId configured.
    /// </summary>
    public long? NumberSequenceId { get; set; }

    /// <summary>
    /// Invoice line items
    /// At least one item is required
    /// </summary>
    [Required]
    [MinLength(1, ErrorMessage = "At least one invoice item is required")]
    public List<CreateInvoiceItemDto> InvoiceItem { get; set; } = new();
}

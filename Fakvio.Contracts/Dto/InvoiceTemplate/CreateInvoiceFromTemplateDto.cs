using System.ComponentModel.DataAnnotations;

namespace Fakvio.Contracts.Dto.InvoiceTemplate;

/// <summary>
/// DTO for creating an invoice from a template
/// Allows overriding template values
/// </summary>
public class CreateInvoiceFromTemplateDto
{
    /// <summary>
    /// Client ID (who receives the invoice)
    /// Required - templates don't have a default client
    /// </summary>
    [Required]
    public long ClientId { get; set; }

    /// <summary>
    /// Issue date - when the invoice is created
    /// If not provided, today's date is used
    /// </summary>
    public DateTime? IssueDate { get; set; }

    /// <summary>
    /// Due date - when payment is due
    /// If not provided, calculated from template's DueDateOffsetDays
    /// </summary>
    public DateTime? DueDate { get; set; }

    /// <summary>
    /// Date of taxable supply (DUZP)
    /// If not provided, same as IssueDate
    /// </summary>
    public DateTime? TaxableSupplyDate { get; set; }

    /// <summary>
    /// Override template's variable symbol — Czech banking requires max 10 digits only.
    /// </summary>
    [StringLength(10)]
    [RegularExpression(@"^\d{0,10}$", ErrorMessage = "Variable symbol must contain only digits (max 10)")]
    public string? VariableSymbol { get; set; }

    /// <summary>
    /// Override template's notes
    /// </summary>
    [StringLength(5000)]
    public string? Notes { get; set; }

    /// <summary>
    /// Required when creating a CreditNote from a CreditNote template.
    /// Identifies the original Invoice that this credit note cancels or partially refunds.
    /// Czech accounting: credit note (dobropis) must reference the original invoice.
    /// Null for regular Invoice templates.
    /// </summary>
    public long? OriginalInvoiceId { get; set; }

    /// <summary>
    /// Optional override for the template's bank account — a BankAccount.Id belonging to the
    /// template's issuer. When set, wins over the template's own BankAccountNumber/IBAN/SWIFT.
    /// Validated the same way as CreateInvoiceDto.BankAccountId (400 if it belongs to another issuer).
    /// </summary>
    public long? BankAccountId { get; set; }

    /// <summary>
    /// Should the invoice be automatically completed (not Draft)?
    /// If false, invoice is created in Draft status
    /// If true, invoice is created and immediately completed (assigned document number)
    /// </summary>
    public bool AutoComplete { get; set; } = false;

    /// <summary>
    /// Months to move billing periods named in the template texts ("Hosting 3/2026" → "4/2026") forward.
    /// Set by the recurring-invoice worker; 0 (default) leaves texts untouched.
    /// </summary>
    public int ShiftMonths { get; set; }
}

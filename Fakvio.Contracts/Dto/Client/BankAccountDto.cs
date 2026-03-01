using System.ComponentModel.DataAnnotations;

namespace Fakvio.Contracts.Dto.Client;

/// <summary>
/// DTO for reading bank account data (API response).
/// Contains all bank account fields plus the entity ID.
/// </summary>
public class BankAccountDto
{
    /// <summary>
    /// Unique identifier of this bank account
    /// </summary>
    public long Id { get; set; }

    /// <summary>
    /// Optional label to identify this account (e.g., "CZK Account", "EUR Business")
    /// </summary>
    public string? Label { get; set; }

    /// <summary>
    /// Bank name (e.g., "Fio banka", "CSOB")
    /// </summary>
    public string? BankName { get; set; }

    /// <summary>
    /// Bank account number (required).
    /// Czech format: "1234567890/0100"
    /// </summary>
    public string AccountNumber { get; set; } = string.Empty;

    /// <summary>
    /// IBAN for international payments
    /// </summary>
    public string? IBAN { get; set; }

    /// <summary>
    /// SWIFT/BIC code for international transfers
    /// </summary>
    public string? SWIFT { get; set; }

    /// <summary>
    /// Currency code (e.g., "CZK", "EUR")
    /// </summary>
    public string? CurrencyCode { get; set; }

    /// <summary>
    /// Is this the default account for this client?
    /// </summary>
    public bool IsDefault { get; set; }
}

/// <summary>
/// DTO for creating a new bank account.
/// AccountNumber is the only required field.
/// </summary>
public class CreateBankAccountDto
{
    /// <summary>
    /// Optional label to identify this account
    /// </summary>
    [StringLength(200)]
    public string? Label { get; set; }

    /// <summary>
    /// Bank name
    /// </summary>
    [StringLength(200)]
    public string? BankName { get; set; }

    /// <summary>
    /// Bank account number (required)
    /// </summary>
    [Required(ErrorMessage = "Account number is required")]
    [StringLength(100, MinimumLength = 1)]
    public string AccountNumber { get; set; } = string.Empty;

    /// <summary>
    /// IBAN for international payments
    /// </summary>
    [StringLength(50)]
    public string? IBAN { get; set; }

    /// <summary>
    /// SWIFT/BIC code
    /// </summary>
    [StringLength(20)]
    public string? SWIFT { get; set; }

    /// <summary>
    /// Currency code (e.g., "CZK", "EUR")
    /// </summary>
    [StringLength(3)]
    public string? CurrencyCode { get; set; }

    /// <summary>
    /// Mark this as the default account
    /// </summary>
    public bool IsDefault { get; set; } = false;
}

/// <summary>
/// DTO for updating an existing bank account.
/// All fields are nullable — only provided fields are updated.
/// </summary>
public class UpdateBankAccountDto
{
    /// <summary>
    /// Optional label to identify this account
    /// </summary>
    [StringLength(200)]
    public string? Label { get; set; }

    /// <summary>
    /// Bank name
    /// </summary>
    [StringLength(200)]
    public string? BankName { get; set; }

    /// <summary>
    /// Bank account number
    /// </summary>
    [StringLength(100)]
    public string? AccountNumber { get; set; }

    /// <summary>
    /// IBAN for international payments
    /// </summary>
    [StringLength(50)]
    public string? IBAN { get; set; }

    /// <summary>
    /// SWIFT/BIC code
    /// </summary>
    [StringLength(20)]
    public string? SWIFT { get; set; }

    /// <summary>
    /// Currency code
    /// </summary>
    [StringLength(3)]
    public string? CurrencyCode { get; set; }

    /// <summary>
    /// Mark this as the default account
    /// </summary>
    public bool? IsDefault { get; set; }
}

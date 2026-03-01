using Fakvio.Domain.Common;

namespace Fakvio.Domain.Entities;

/// <summary>
/// Represents one bank account of a client (issuer or customer).
/// A client can have multiple bank accounts (CZK account, EUR account, etc.).
/// Follows the same 1:N pattern as Address and Contact.
///
/// For issuers: these are the accounts that appear on invoices as payment destination.
/// For customers: these could be stored for reference (optional future feature).
///
/// One account per client can be marked as IsDefault — this is the account
/// automatically selected when creating a new invoice.
/// </summary>
public class BankAccount : BaseEntity
{
    /// <summary>
    /// Foreign key to Client.
    /// Which client does this bank account belong to.
    /// </summary>
    public long ClientId { get; set; }

    /// <summary>
    /// Navigation property to Client.
    /// </summary>
    public Client Client { get; set; } = null!;

    /// <summary>
    /// Optional human-readable label for this account.
    /// Example: "CZK Account", "EUR Business", "Savings"
    /// Helps users distinguish between multiple accounts in the dropdown.
    /// </summary>
    public string? Label { get; set; }

    /// <summary>
    /// Optional bank name.
    /// Example: "Fio banka", "CSOB", "Komercni banka"
    /// </summary>
    public string? BankName { get; set; }

    /// <summary>
    /// Bank account number (required).
    /// Czech format: "1234567890/0100" (account number / bank code)
    /// International format: could also be a plain account number.
    /// </summary>
    public string AccountNumber { get; set; } = string.Empty;

    /// <summary>
    /// International Bank Account Number (optional).
    /// Example: "CZ65 0800 0000 0019 2000 0145"
    /// Used for international payments and QR code generation (QR Platba / SPD).
    /// </summary>
    public string? IBAN { get; set; }

    /// <summary>
    /// SWIFT/BIC code (optional).
    /// Example: "GIBACZPX"
    /// Used for international wire transfers.
    /// </summary>
    public string? SWIFT { get; set; }

    /// <summary>
    /// Currency code for this account (optional).
    /// Example: "CZK", "EUR", "USD"
    /// Helps users pick the right account when creating invoices in a specific currency.
    /// </summary>
    public string? CurrencyCode { get; set; }

    /// <summary>
    /// Is this the default bank account for this client?
    /// Only one account per client should be marked as default.
    /// The default account is auto-selected when creating a new invoice.
    /// </summary>
    public bool IsDefault { get; set; } = false;
}

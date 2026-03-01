namespace Fakvio.Domain.Enums;

/// <summary>
/// Type of contact information (phone, email, etc.)
/// Each client can have multiple contacts of different types
/// </summary>
public enum EContactType
{
    /// <summary>
    /// Primary email address for general communication
    /// </summary>
    Email = 1,

    /// <summary>
    /// Mobile phone number
    /// </summary>
    Phone = 2,

    /// <summary>
    /// Email specifically for receiving invoices
    /// If set, invoices should be sent to this email instead of primary
    /// </summary>
    InvoiceEmail = 3,

    /// <summary>
    /// Fax number (legacy, but some companies still use it)
    /// </summary>
    Fax = 4,

    /// <summary>
    /// Website URL
    /// </summary>
    Website = 5
}

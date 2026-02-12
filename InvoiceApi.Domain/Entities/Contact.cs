using InvoiceApi.Domain.Common;
using InvoiceApi.Domain.Enums;

namespace InvoiceApi.Domain.Entities;

/// <summary>
/// Represents one contact information of a client
/// A client can have multiple contacts (emails, phones, etc.)
/// </summary>
public class Contact : BaseEntity
{
    /// <summary>
    /// Foreign key to Client
    /// Which client does this contact belong to
    /// </summary>
    public long ClientId { get; set; }

    /// <summary>
    /// Navigation property to Client
    /// </summary>
    public Client Client { get; set; } = null!;

    /// <summary>
    /// Type of contact information
    /// Examples: Email, Phone, InvoiceEmail, Fax, Website
    /// </summary>
    public EContactType ContactType { get; set; }

    /// <summary>
    /// The actual contact value
    /// Examples:
    ///   For Email: "info@company.com"
    ///   For Phone: "+420123456789"
    ///   For Website: "https://www.company.com"
    /// </summary>
    public string ContactValue { get; set; } = string.Empty;

    /// <summary>
    /// Optional label/description for this contact
    /// Example: "Main office", "CEO", "Accounting department"
    /// Helps identify who or what this contact is for
    /// </summary>
    public string? Label { get; set; }

    /// <summary>
    /// Is this the primary/default contact for this type?
    /// Used when client has multiple contacts of same type
    /// Example: Multiple emails - one is marked as primary
    /// </summary>
    public bool IsPrimary { get; set; } = false;
}

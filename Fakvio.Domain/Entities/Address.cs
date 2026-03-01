using Fakvio.Domain.Common;
using Fakvio.Domain.Enums;

namespace Fakvio.Domain.Entities;

/// <summary>
/// Represents one address of a client
/// A client can have multiple addresses (primary, billing, shipping, etc.)
/// </summary>
public class Address : BaseEntity
{
    /// <summary>
    /// Foreign key to Client
    /// Which client does this address belong to
    /// </summary>
    public long ClientId { get; set; }

    /// <summary>
    /// Navigation property to Client
    /// </summary>
    public Client Client { get; set; } = null!;

    /// <summary>
    /// Type of this address
    /// Examples: Primary (registered office), Billing, Shipping, Correspondence
    /// </summary>
    public EAddressType AddressType { get; set; }

    /// <summary>
    /// Street name and building number
    /// Example: "Main Street 123"
    /// </summary>
    public string Street { get; set; } = string.Empty;

    /// <summary>
    /// City name
    /// Example: "Prague"
    /// </summary>
    public string City { get; set; } = string.Empty;

    /// <summary>
    /// Postal/ZIP code
    /// Example: "120 00"
    /// </summary>
    public string PostalCode { get; set; } = string.Empty;

    /// <summary>
    /// Country name or code
    /// Example: "Czech Republic" or "CZ"
    /// </summary>
    public string Country { get; set; } = string.Empty;

    /// <summary>
    /// Optional additional address line
    /// For apartment numbers, building names, etc.
    /// Example: "Building A, 3rd floor"
    /// </summary>
    public string? AddressLine2 { get; set; }

    /// <summary>
    /// Is this the primary/default address for this type?
    /// Used when client has multiple addresses of same type
    /// </summary>
    public bool IsPrimary { get; set; } = false;
}

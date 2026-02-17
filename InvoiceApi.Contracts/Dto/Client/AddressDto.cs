using InvoiceApi.Domain.Enums;

namespace InvoiceApi.Contracts.Dto.Client;

/// <summary>
/// DTO for address data
/// </summary>
public class AddressDto
{
    public long Id { get; set; }
    public EAddressType AddressType { get; set; }
    public string Street { get; set; } = string.Empty;
    public string City { get; set; } = string.Empty;
    public string PostalCode { get; set; } = string.Empty;
    public string Country { get; set; } = string.Empty;
    public string? AddressLine2 { get; set; }
    public bool IsPrimary { get; set; }
}

/// <summary>
/// DTO for creating a new address
/// </summary>
public class CreateAddressDto
{
    public EAddressType AddressType { get; set; }
    public string Street { get; set; } = string.Empty;
    public string City { get; set; } = string.Empty;
    public string PostalCode { get; set; } = string.Empty;
    public string Country { get; set; } = "Czech Republic";
    public string? AddressLine2 { get; set; }
    public bool IsPrimary { get; set; } = false;
}

/// <summary>
/// DTO for updating an existing address
/// </summary>
public class UpdateAddressDto
{
    public EAddressType? AddressType { get; set; }
    public string? Street { get; set; }
    public string? City { get; set; }
    public string? PostalCode { get; set; }
    public string? Country { get; set; }
    public string? AddressLine2 { get; set; }
    public bool? IsPrimary { get; set; }
}

using InvoiceApi.Domain.Enums;

namespace InvoiceApi.Application.Dto.Client;

/// <summary>
/// DTO for contact data
/// </summary>
public class ContactDto
{
    public long Id { get; set; }
    public EContactType ContactType { get; set; }
    public string ContactValue { get; set; } = string.Empty;
    public string? Label { get; set; }
    public bool IsPrimary { get; set; }
}

/// <summary>
/// DTO for creating a new contact
/// </summary>
public class CreateContactDto
{
    public EContactType ContactType { get; set; }
    public string ContactValue { get; set; } = string.Empty;
    public string? Label { get; set; }
    public bool IsPrimary { get; set; } = false;
}

/// <summary>
/// DTO for updating an existing contact
/// </summary>
public class UpdateContactDto
{
    public EContactType? ContactType { get; set; }
    public string? ContactValue { get; set; }
    public string? Label { get; set; }
    public bool? IsPrimary { get; set; }
}

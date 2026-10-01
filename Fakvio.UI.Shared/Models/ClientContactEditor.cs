using Fakvio.Contracts.Dto.Client;
using Fakvio.Domain.Enums;
namespace Fakvio.UI.Shared.Models;

/// <summary>Edits the visible contact fields while preserving contacts the form does not expose.</summary>
public static class ClientContactEditor
{
    public static List<UpdateContactDto> Merge(IEnumerable<ContactDto> original, string? email, string? phone)
    {
        // The API replaces the collection, so keep every untouched contact and its metadata.
        var contacts = original.Select(c => new UpdateContactDto
        {
            ContactType = c.ContactType, ContactValue = c.ContactValue,
            Label = c.Label, IsPrimary = c.IsPrimary
        }).ToList();
        UpdateFirst(contacts, EContactType.Email, email);
        UpdateFirst(contacts, EContactType.Phone, phone);
        return contacts;
    }

    private static void UpdateFirst(List<UpdateContactDto> contacts, EContactType type, string? value)
    {
        var contact = contacts.FirstOrDefault(c => c.ContactType == type);
        if (string.IsNullOrWhiteSpace(value))
        {
            if (contact is not null) contacts.Remove(contact);
        }
        else if (contact is not null) contact.ContactValue = value;
        else contacts.Add(new UpdateContactDto
        {
            ContactType = type, ContactValue = value, IsPrimary = contacts.Count == 0
        });
    }
}

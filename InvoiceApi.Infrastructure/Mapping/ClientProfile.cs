using InvoiceApi.Contracts.Dto.Client;
using InvoiceApi.Domain.Entities;
using ZMapper;

namespace InvoiceApi.Infrastructure.Mapping;

/// <summary>
/// ZMapper profile for Client entity → ClientDto mapping.
/// Client has nested collections (Address, Contact) and optional BillingSettings.
/// All nested type mappings are registered so ZMapper can map them automatically.
/// ZMapper v1.1.0 now maps inherited BaseEntity properties (Id, CreatedAt, UpdatedAt)
/// and nested collection Ids without any manual intervention.
/// </summary>
public partial class ClientProfile : IMapperProfile
{
    public void Configure(MapperConfiguration config)
    {
        // Address → AddressDto: direct 1:1 mapping including inherited Id
        config.CreateMap<Address, AddressDto>();

        // Contact → ContactDto: direct 1:1 mapping including inherited Id
        config.CreateMap<Contact, ContactDto>();

        // BankAccount → BankAccountDto: direct 1:1 mapping including inherited Id
        config.CreateMap<BankAccount, BankAccountDto>();

        // BillingSettings → BillingSettingsDto: direct 1:1 mapping including inherited Id
        config.CreateMap<BillingSettings, BillingSettingsDto>();

        // Client → ClientDto: nested Address/Contact/BankAccount collections and BillingSettings
        // are mapped automatically because their type mappings are registered above
        config.CreateMap<Client, ClientDto>();
    }
}

using Fakvio.Contracts.Dto.Client;
using Fakvio.Domain.Entities;
using ZMapper;

namespace Fakvio.Infrastructure.Mapping;

/// <summary>
/// ZMapper profile for Client entity ↔ DTO mapping.
/// Contains both read (Entity → DTO) and create (CreateDto → Entity) mappings.
/// Client has nested collections (Address, Contact, BankAccount) and optional BillingSettings.
///
/// Read direction: ZMapper maps all matching properties including inherited BaseEntity (Id, CreatedAt, UpdatedAt).
/// Create direction: navigation properties (Client, CustomInvoiceNumberSequence, etc.) are ignored
/// because they are set by EF Core via foreign keys, not by the caller.
/// BaseEntity properties (Id, CreatedAt, UpdatedAt, etc.) are automatically skipped
/// because they don't exist on the create DTOs.
/// </summary>
public partial class ClientProfile : IMapperProfile
{
    public void Configure(MapperConfiguration config)
    {
        // =====================================================================
        // READ direction: Entity → DTO (for API responses)
        // =====================================================================

        // Address → AddressDto: direct 1:1 mapping including inherited Id
        config.CreateMap<Address, AddressDto>();

        // Contact → ContactDto: direct 1:1 mapping including inherited Id
        config.CreateMap<Contact, ContactDto>();

        // BankAccount → BankAccountDto: direct 1:1 mapping including inherited Id
        config.CreateMap<BankAccount, BankAccountDto>();

        // BillingSettings → BillingSettingsDto: direct 1:1 mapping including inherited Id
        config.CreateMap<BillingSettings, BillingSettingsDto>();

        // Client → ClientDto: nested Address/Contact/BankAccount collections and BillingSettings
        // are mapped automatically because their type mappings are registered above.
        // Tax regime enum fields (ETaxRegime? → string?) cannot be auto-mapped by ZMapper,
        // so we ignore them here and handle them manually in ClientService.MapToDto().
        config.CreateMap<Client, ClientDto>()
            .ForMember(d => d.TaxRegime, opt => opt.Ignore())
            .ForMember(d => d.ActivityType, opt => opt.Ignore())
            .ForMember(d => d.FlatRateBand, opt => opt.Ignore());

        // =====================================================================
        // CREATE direction: CreateDto → Entity (for creating new records)
        // Navigation properties are ignored — EF Core sets them via FK relationships.
        // BaseEntity properties (Id, CreatedAt, etc.) are auto-skipped because
        // they don't exist on the source DTOs.
        // =====================================================================

        // CreateAddressDto → Address: maps AddressType, Street, City, PostalCode, Country,
        // AddressLine2, IsPrimary. ClientId is set separately in service layer.
        // IgnoreNonExisting: BaseEntity properties (Id, CreatedAt, etc.) don't exist on the source DTO.
        config.CreateMap<CreateAddressDto, Address>()
            .ForMember(d => d.Client, opt => opt.Ignore())
            .IgnoreNonExisting();

        // CreateContactDto → Contact: maps ContactType, ContactValue, Label, IsPrimary.
        // ClientId is set separately in service layer.
        config.CreateMap<CreateContactDto, Contact>()
            .ForMember(d => d.Client, opt => opt.Ignore())
            .IgnoreNonExisting();

        // CreateBankAccountDto → BankAccount: maps Label, BankName, AccountNumber, IBAN,
        // SWIFT, CurrencyCode, IsDefault. ClientId is set separately in service layer.
        config.CreateMap<CreateBankAccountDto, BankAccount>()
            .ForMember(d => d.Client, opt => opt.Ignore())
            .IgnoreNonExisting();

        // CreateBillingSettingsDto → BillingSettings: maps DueDateCalculationType, DueDays,
        // CustomInvoiceNumberSequenceId, CustomCreditNoteNumberSequenceId, prefixes/suffixes,
        // DefaultPaymentMethod, BankAccountNumber, Notes.
        // Navigation properties are ignored — only FK Ids are mapped from the DTO.
        config.CreateMap<CreateBillingSettingsDto, BillingSettings>()
            .ForMember(d => d.Client, opt => opt.Ignore())
            .ForMember(d => d.CustomInvoiceNumberSequence, opt => opt.Ignore())
            .ForMember(d => d.CustomCreditNoteNumberSequence, opt => opt.Ignore())
            .IgnoreNonExisting();
    }
}

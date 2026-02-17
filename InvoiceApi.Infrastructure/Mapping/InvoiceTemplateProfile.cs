using InvoiceApi.Contracts.Dto.InvoiceTemplate;
using InvoiceApi.Domain.Entities;
using ZMapper;

namespace InvoiceApi.Infrastructure.Mapping;

/// <summary>
/// ZMapper profile for InvoiceTemplate entity → InvoiceTemplateDto mapping.
/// ZMapper v1.1.0 maps all properties including inherited BaseEntity (Id, CreatedAt, UpdatedAt).
/// Navigation-derived properties (IssuerName, CurrencyCode, CurrencySymbol) are explicitly
/// ignored here and set manually in the service layer.
/// </summary>
public partial class InvoiceTemplateProfile : IMapperProfile
{
    public void Configure(MapperConfiguration config)
    {
        config.CreateMap<InvoiceTemplate, InvoiceTemplateDto>()
            .ForMember(d => d.IssuerName, opt => opt.Ignore())
            .ForMember(d => d.ClientName, opt => opt.Ignore())
            .ForMember(d => d.CurrencyCode, opt => opt.Ignore())
            .ForMember(d => d.CurrencySymbol, opt => opt.Ignore())
            .ForMember(d => d.NumberSequenceName, opt => opt.Ignore());
    }
}

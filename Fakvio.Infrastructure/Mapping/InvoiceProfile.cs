using Fakvio.Contracts.Dto.Invoice;
using Fakvio.Domain.Entities;
using ZMapper;

namespace Fakvio.Infrastructure.Mapping;

/// <summary>
/// ZMapper profile for Invoice entity → InvoiceDto mapping.
/// ZMapper v1.1.0 maps all properties including inherited BaseEntity (Id, CreatedAt, UpdatedAt)
/// and nested InvoiceItem collection with their Ids automatically.
/// Navigation-derived properties (ClientName, IssuerName, CurrencyCode, CurrencySymbol,
/// OriginalInvoiceNumber) are explicitly ignored here and set manually in the service layer
/// because they are flattened from related entities.
/// </summary>
public partial class InvoiceProfile : IMapperProfile
{
    public void Configure(MapperConfiguration config)
    {
        // InvoiceItem → InvoiceItemDto: direct 1:1 mapping including inherited Id
        config.CreateMap<InvoiceItem, InvoiceItemDto>();

        // Invoice → InvoiceDto: direct property mapping for matching properties.
        // Navigation-derived props are ignored (set in service layer after mapping).
        config.CreateMap<Invoice, InvoiceDto>()
            .ForMember(d => d.ClientName, opt => opt.Ignore())
            .ForMember(d => d.IssuerName, opt => opt.Ignore())
            .ForMember(d => d.CurrencyCode, opt => opt.Ignore())
            .ForMember(d => d.CurrencySymbol, opt => opt.Ignore())
            .ForMember(d => d.OriginalInvoiceNumber, opt => opt.Ignore());
    }
}

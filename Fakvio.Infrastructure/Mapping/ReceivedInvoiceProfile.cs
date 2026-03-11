using Fakvio.Contracts.Dto.ReceivedInvoice;
using Fakvio.Domain.Entities;
using ZMapper;

namespace Fakvio.Infrastructure.Mapping;

/// <summary>
/// ZMapper profile for ReceivedInvoice entity -> ReceivedInvoiceDto mapping.
/// Navigation-derived properties (SupplierName, CurrencyCode, CurrencySymbol)
/// are ignored here and set manually in the service layer.
/// </summary>
public partial class ReceivedInvoiceProfile : IMapperProfile
{
    public void Configure(MapperConfiguration config)
    {
        // ReceivedInvoiceItem -> ReceivedInvoiceItemDto: direct 1:1 mapping
        config.CreateMap<ReceivedInvoiceItem, ReceivedInvoiceItemDto>();

        // ReceivedInvoice -> ReceivedInvoiceDto: direct property mapping.
        // Navigation-derived props are set manually in service layer after mapping.
        config.CreateMap<ReceivedInvoice, ReceivedInvoiceDto>()
            .ForMember(d => d.SupplierName, opt => opt.Ignore())
            .ForMember(d => d.CurrencyCode, opt => opt.Ignore())
            .ForMember(d => d.CurrencySymbol, opt => opt.Ignore());
    }
}

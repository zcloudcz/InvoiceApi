using Fakvio.Contracts.Dto.ReceivedInvoice;
using Fakvio.Domain.Entities;
using ZMapper;

namespace Fakvio.Infrastructure.Mapping;

/// <summary>
/// ZMapper profile for the received-invoice facade. A received invoice is an <see cref="Invoice"/> row
/// (DocumentType = ReceivedInvoice), so only the line items map 1:1 (InvoiceItem -> ReceivedInvoiceItemDto).
/// The header is mapped by hand in <c>ReceivedInvoiceService.MapToDto</c> because the DTO keeps the old
/// shape (SupplierId, EReceivedInvoiceStatus) that ZMapper's name-for-name copy cannot express.
/// </summary>
public partial class ReceivedInvoiceProfile : IMapperProfile
{
    public void Configure(MapperConfiguration config)
    {
        config.CreateMap<InvoiceItem, ReceivedInvoiceItemDto>();
    }
}

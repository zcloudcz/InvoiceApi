using InvoiceApi.Contracts.Dto.VatRate;
using InvoiceApi.Domain.Entities;
using ZMapper;

namespace InvoiceApi.Infrastructure.Mapping;

/// <summary>
/// ZMapper profile for VatRate entity → VatRateDto mapping.
/// ZMapper v1.1.0 maps all properties including inherited BaseEntity (Id, CreatedAt, UpdatedAt).
/// No manual property assignments are needed.
/// </summary>
public partial class VatRateProfile : IMapperProfile
{
    public void Configure(MapperConfiguration config)
    {
        config.CreateMap<VatRate, VatRateDto>();
    }
}

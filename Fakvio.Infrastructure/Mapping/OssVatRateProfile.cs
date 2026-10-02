using Fakvio.Contracts.Dto.OssVatRate;
using Fakvio.Domain.Entities;
using ZMapper;

namespace Fakvio.Infrastructure.Mapping;

/// <summary>
/// ZMapper profile for OssVatRate entity → OssVatRateDto mapping.
/// ZMapper v1.1.0 maps all properties including inherited BaseEntity (Id, CreatedAt, UpdatedAt).
/// </summary>
public partial class OssVatRateProfile : IMapperProfile
{
    public void Configure(MapperConfiguration config)
    {
        config.CreateMap<OssVatRate, OssVatRateDto>();
    }
}

using Fakvio.Contracts.Dto.ReverseChargeCode;
using Fakvio.Domain.Entities;
using ZMapper;

namespace Fakvio.Infrastructure.Mapping;

/// <summary>
/// ZMapper profile for ReverseChargeCode entity → ReverseChargeCodeDto mapping.
/// ZMapper v1.1.0 maps all properties including inherited BaseEntity (Id, CreatedAt, UpdatedAt).
/// </summary>
public partial class ReverseChargeCodeProfile : IMapperProfile
{
    public void Configure(MapperConfiguration config)
    {
        config.CreateMap<ReverseChargeCode, ReverseChargeCodeDto>();
    }
}

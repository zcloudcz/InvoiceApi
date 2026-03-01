using Fakvio.Contracts.Dto.Currency;
using Fakvio.Domain.Entities;
using ZMapper;

namespace Fakvio.Infrastructure.Mapping;

/// <summary>
/// ZMapper profile for Currency entity → CurrencyDto mapping.
/// ZMapper v1.1.0 maps all properties including inherited BaseEntity (Id, CreatedAt, UpdatedAt).
/// No manual property assignments are needed.
/// </summary>
public partial class CurrencyProfile : IMapperProfile
{
    public void Configure(MapperConfiguration config)
    {
        config.CreateMap<Currency, CurrencyDto>();
    }
}

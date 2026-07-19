using Fakvio.Contracts.Dto.RecognizedCounterparty;
using Fakvio.Domain.Entities;
using ZMapper;

namespace Fakvio.Infrastructure.Mapping;

/// <summary>
/// ZMapper profile for RecognizedCounterparty entity ↔ DTO mapping.
/// ZMapper v1.1.0 maps all properties including inherited BaseEntity (Id, CreatedAt, UpdatedAt).
/// </summary>
public partial class RecognizedCounterpartyProfile : IMapperProfile
{
    public void Configure(MapperConfiguration config)
    {
        config.CreateMap<RecognizedCounterparty, RecognizedCounterpartyDto>();
        config.CreateMap<SaveRecognizedCounterpartyRequest, RecognizedCounterparty>();
    }
}

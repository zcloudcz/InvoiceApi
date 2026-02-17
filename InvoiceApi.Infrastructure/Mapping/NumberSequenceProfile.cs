using InvoiceApi.Contracts.Dto.NumberSequence;
using InvoiceApi.Domain.Entities;
using ZMapper;

namespace InvoiceApi.Infrastructure.Mapping;

/// <summary>
/// ZMapper profile for NumberSequence and NumberSequenceFormat mappings.
/// ZMapper v1.1.0 maps all properties including inherited BaseEntity (Id)
/// and nested NumberSequenceFormat automatically.
/// </summary>
public partial class NumberSequenceProfile : IMapperProfile
{
    public void Configure(MapperConfiguration config)
    {
        // NumberSequenceFormat → NumberSequenceFormatDto: direct 1:1 mapping including Id
        config.CreateMap<NumberSequenceFormat, NumberSequenceFormatDto>();

        // NumberSequence → NumberSequenceDto: includes nested NumberSequenceFormat
        config.CreateMap<NumberSequence, NumberSequenceDto>();
    }
}

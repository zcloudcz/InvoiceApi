using InvoiceApi.Application.Dto.ContentTemplate;
using InvoiceApi.Domain.Entities;
using ZMapper;

namespace InvoiceApi.Infrastructure.Mapping;

/// <summary>
/// ZMapper profile for ContentTemplate entity -> ContentTemplateDto mapping.
/// All properties including inherited BaseEntity (Id, CreatedAt, UpdatedAt)
/// are mapped automatically by ZMapper v1.2.0.
/// </summary>
public partial class ContentTemplateProfile : IMapperProfile
{
    public void Configure(MapperConfiguration config)
    {
        config.CreateMap<ContentTemplate, ContentTemplateDto>();
    }
}

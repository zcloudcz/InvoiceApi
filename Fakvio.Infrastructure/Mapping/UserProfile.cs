using Fakvio.Contracts.Dto.User;
using Fakvio.Domain.Entities;
using ZMapper;

namespace Fakvio.Infrastructure.Mapping;

/// <summary>
/// ZMapper profile for User entity → UserDto mapping.
/// ZMapper v1.1.0+ maps all properties including inherited BaseEntity (Id, CreatedAt, UpdatedAt).
/// Navigation-derived property CompanyName, computed properties FullName and IsExternalLogin
/// are explicitly ignored here and set manually in the service layer.
/// </summary>
public partial class UserProfile : IMapperProfile
{
    public void Configure(MapperConfiguration config)
    {
        config.CreateMap<User, UserDto>()
            // Navigation-derived — set manually in UserService.MapToDto()
            .ForMember(d => d.CompanyName, opt => opt.Ignore())
            // Computed properties — set manually in UserService.MapToDto()
            .ForMember(d => d.FullName, opt => opt.Ignore())
            .ForMember(d => d.IsExternalLogin, opt => opt.Ignore());
    }
}

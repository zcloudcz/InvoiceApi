using Fakvio.Contracts.Dto.Reminder;
using Fakvio.Domain.Entities;
using ZMapper;

namespace Fakvio.Infrastructure.Mapping;

/// <summary>
/// ZMapper profile for Reminder-related entity → DTO mapping.
/// Navigation-derived properties (InvoiceNumber, ClientName) are ignored here
/// and set manually in the service layer after mapping.
/// </summary>
public partial class ReminderProfile : IMapperProfile
{
    public void Configure(MapperConfiguration config)
    {
        // ReminderLevel -> ReminderLevelDto: direct 1:1 mapping.
        config.CreateMap<ReminderLevel, ReminderLevelDto>();

        // Reminder -> ReminderDto: direct property mapping.
        // InvoiceNumber and ClientName come from navigation properties — set in service.
        config.CreateMap<Reminder, ReminderDto>()
            .ForMember(d => d.InvoiceNumber, opt => opt.Ignore())
            .ForMember(d => d.ClientName, opt => opt.Ignore());
    }
}

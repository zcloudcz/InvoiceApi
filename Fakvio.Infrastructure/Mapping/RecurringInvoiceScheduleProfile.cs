using Fakvio.Contracts.Dto.RecurringInvoice;
using Fakvio.Domain.Entities;
using ZMapper;

namespace Fakvio.Infrastructure.Mapping;

/// <summary>
/// ZMapper profile for RecurringInvoiceSchedule -> RecurringInvoiceScheduleDto mapping.
/// TemplateName/ClientName come from navigation properties — set manually in the service
/// after mapping (same pattern as ReminderProfile).
/// </summary>
public partial class RecurringInvoiceScheduleProfile : IMapperProfile
{
    public void Configure(MapperConfiguration config)
    {
        config.CreateMap<RecurringInvoiceSchedule, RecurringInvoiceScheduleDto>()
            .ForMember(d => d.TemplateName, opt => opt.Ignore())
            .ForMember(d => d.ClientName, opt => opt.Ignore());
    }
}

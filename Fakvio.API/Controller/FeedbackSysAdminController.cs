using Fakvio.Application.Service;
using Fakvio.Contracts.Common.Pagination;
using Fakvio.Contracts.Dto.Feedback;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Fakvio.API.Controller;

/// <summary>Central inbox. Both HTTP authorization and the service require the SysAdmin role.</summary>
[ApiController]
[Authorize(Roles = "SysAdmin")]
[Route("api/sysadmin/feedback")]
[FeedbackErrors]
public sealed class FeedbackSysAdminController(IFeedbackService service) : ControllerBase
{
    [HttpGet]
    public async Task<ActionResult<PagedResult<FeedbackDto>>> List([FromQuery] FeedbackFilterDto filter, CancellationToken ct)
        => Ok(await service.ListAllAsync(filter, ct));

    [HttpGet("{id:long}")]
    public async Task<ActionResult<FeedbackDto>> Get(long id, CancellationToken ct)
    {
        var report = await service.GetAnyAsync(id, ct);
        return report is null ? NotFound() : Ok(report);
    }

    [HttpPatch("{id:long}")]
    public async Task<ActionResult<FeedbackDto>> Update(long id, UpdateFeedbackStatusDto input, CancellationToken ct)
    {
        var report = await service.UpdateAsync(id, input, ct);
        return report is null ? NotFound() : Ok(report);
    }
}

using System.ComponentModel.DataAnnotations;
using Fakvio.Application.Service;
using Fakvio.Contracts.Common.Pagination;
using Fakvio.Contracts.Dto.Feedback;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;

namespace Fakvio.API.Controller;

/// <summary>Authenticated personal feedback. Owner and company never come from the request body.</summary>
[ApiController]
[Authorize]
[Route("api/feedback")]
[FeedbackErrors]
public sealed class FeedbackController(IFeedbackService service) : ControllerBase
{
    [HttpPost]
    public async Task<ActionResult<FeedbackDto>> Create(CreateFeedbackDto input, CancellationToken ct)
    {
        var report = await service.CreateAsync(input, ct);
        return CreatedAtAction(nameof(Get), new { id = report.Id }, report);
    }

    [HttpGet]
    public async Task<ActionResult<PagedResult<FeedbackDto>>> List([FromQuery] FeedbackFilterDto filter, CancellationToken ct)
        => Ok(await service.ListMineAsync(filter, ct));

    [HttpGet("{id:long}")]
    public async Task<ActionResult<FeedbackDto>> Get(long id, CancellationToken ct)
    {
        var report = await service.GetMineAsync(id, ct);
        // Do not reveal whether an inaccessible report exists.
        return report is null ? NotFound() : Ok(report);
    }
}

/// <summary>
/// Maps expected feedback validation and access failures to HTTP errors. Unexpected
/// failures still reach the global exception logger; submitted text is never logged here.
/// </summary>
internal sealed class FeedbackErrorsAttribute : ExceptionFilterAttribute
{
    public override void OnException(ExceptionContext context)
    {
        context.Result = context.Exception switch
        {
            ValidationException ex => new BadRequestObjectResult(new { message = ex.Message }),
            UnauthorizedAccessException => new ObjectResult(new { message = "Access denied for the current user or company." }) { StatusCode = 403 },
            _ => null
        };
        context.ExceptionHandled = context.Result is not null;
    }
}

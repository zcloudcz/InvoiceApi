using Fakvio.Application.Service;
using Fakvio.Contracts.Dto.SystemConfiguration;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Fakvio.API.Controller;

/// <summary>
/// SysAdmin management of the editable AI assistant system prompt.
///
/// The prompt has two editable parts:
/// - CustomPrompt — replaces the built-in style/tools/rules block.
/// - Appendix — extra rules appended after the main block.
///
/// The route sits under /api/system-configuration, which TenantContextMiddleware treats
/// as master-only — no X-Company-Id header is required.
/// PUT and DELETE invalidate the cache, so a change applies to the next chat message.
/// </summary>
[ApiController]
[Route("api/system-configuration/ai-instructions")]
[Produces("application/json")]
[Authorize(Roles = "SysAdmin")]
public class AiInstructionsController : ControllerBase
{
    private readonly IAiInstructionsService _service;
    private readonly ILogger<AiInstructionsController> _logger;

    public AiInstructionsController(
        IAiInstructionsService service,
        ILogger<AiInstructionsController> logger)
    {
        _service = service;
        _logger = logger;
    }

    /// <summary>
    /// Returns the stored instructions. Both fields are null while the built-in
    /// defaults are in use.
    /// </summary>
    [HttpGet]
    [ProducesResponseType(typeof(AiInstructionsDto), StatusCodes.Status200OK)]
    public async Task<ActionResult<AiInstructionsDto>> Get(CancellationToken ct = default)
    {
        _logger.LogInformation("GET /api/system-configuration/ai-instructions");
        return Ok(await _service.GetAsync(ct));
    }

    /// <summary>
    /// Applies a partial update: null keeps the stored value, an empty string clears it.
    /// </summary>
    [HttpPut]
    [ProducesResponseType(typeof(AiInstructionsDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public async Task<ActionResult<AiInstructionsDto>> Update(
        [FromBody] UpdateAiInstructionsDto dto,
        CancellationToken ct = default)
    {
        _logger.LogInformation("PUT /api/system-configuration/ai-instructions");

        if (!ModelState.IsValid)
            return BadRequest(ModelState);

        return Ok(await _service.UpdateAsync(dto, ct));
    }

    /// <summary>Clears both parts so the built-in defaults apply again.</summary>
    [HttpDelete]
    [ProducesResponseType(typeof(AiInstructionsDto), StatusCodes.Status200OK)]
    public async Task<ActionResult<AiInstructionsDto>> Reset(CancellationToken ct = default)
    {
        _logger.LogInformation("DELETE /api/system-configuration/ai-instructions — resetting to defaults");
        return Ok(await _service.ResetToDefaultAsync(ct));
    }

    /// <summary>
    /// Renders the full system prompt. Tenant-specific parts (company identity, business
    /// statistics) are placeholders, because this is a master-only endpoint.
    /// </summary>
    [HttpGet("preview")]
    [ProducesResponseType(typeof(AiInstructionsPreviewDto), StatusCodes.Status200OK)]
    public async Task<ActionResult<AiInstructionsPreviewDto>> Preview(CancellationToken ct = default)
    {
        _logger.LogInformation("GET /api/system-configuration/ai-instructions/preview");
        return Ok(await _service.GetPreviewAsync(ct));
    }
}

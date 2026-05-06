using Fakvio.Application.Service;
using Fakvio.Contracts.Dto.SystemConfiguration;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Fakvio.API.Controller;

/// <summary>
/// Controller for managing editable AI assistant system prompt instructions.
/// All endpoints are restricted to SysAdmin only.
///
/// The AI assistant has two editable sections in its system prompt:
/// - CustomPrompt: replaces the hardcoded style/rules block entirely.
/// - Appendix: extra instructions appended after the main prompt sections.
///
/// Cache invalidation: PUT and DELETE remove the IMemoryCache entry so the next
/// chat call reads fresh values from the DB.
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
    /// Gets the current custom AI instructions (prompt and appendix).
    /// Returns null values when using hardcoded defaults.
    /// </summary>
    [HttpGet]
    [ProducesResponseType(typeof(AiInstructionsDto), StatusCodes.Status200OK)]
    public async Task<ActionResult<AiInstructionsDto>> Get(CancellationToken ct = default)
    {
        _logger.LogInformation("GET /api/system-configuration/ai-instructions");
        var result = await _service.GetAsync(ct);
        return Ok(result);
    }

    /// <summary>
    /// Updates the custom AI instructions and invalidates the chat context cache.
    /// Partial update: null = keep existing, empty = clear.
    /// Changes take effect on the next AI chat call.
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

        var result = await _service.UpdateAsync(dto, ct);
        return Ok(result);
    }

    /// <summary>
    /// Resets both CustomPrompt and Appendix to null (hardcoded defaults active).
    /// Invalidates the chat context cache so the reset takes effect immediately.
    /// </summary>
    [HttpDelete]
    [ProducesResponseType(typeof(AiInstructionsDto), StatusCodes.Status200OK)]
    public async Task<ActionResult<AiInstructionsDto>> Reset(CancellationToken ct = default)
    {
        _logger.LogInformation("DELETE /api/system-configuration/ai-instructions — resetting to defaults");
        var result = await _service.ResetToDefaultAsync(ct);
        return Ok(result);
    }

    /// <summary>
    /// Returns the full assembled system prompt that the AI would receive.
    /// Business context stats are shown as "[N/A — preview mode]" placeholders because
    /// this endpoint runs in the SysAdmin context (no tenant DB available).
    /// </summary>
    [HttpGet("preview")]
    [ProducesResponseType(typeof(AiInstructionsPreviewDto), StatusCodes.Status200OK)]
    public async Task<ActionResult<AiInstructionsPreviewDto>> Preview(CancellationToken ct = default)
    {
        _logger.LogInformation("GET /api/system-configuration/ai-instructions/preview");
        var result = await _service.GetPreviewAsync(ct);
        return Ok(result);
    }
}

using Fakvio.Contracts.Dto.SystemConfiguration;
using Fakvio.Application.Service;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Fakvio.API.Controller;

/// <summary>
/// Controller for managing global system configuration (SMTP, JWT settings).
/// All endpoints are restricted to SysAdmin only.
///
/// The system configuration is a single-row table in the master database.
/// GET returns the current settings, PUT updates them.
/// </summary>
[ApiController]
[Route("api/system-configuration")]
[Produces("application/json")]
[Authorize(Roles = "SysAdmin")]
public class SystemConfigurationController : ControllerBase
{
    private readonly ISystemConfigurationService _service;
    private readonly ILogger<SystemConfigurationController> _logger;

    public SystemConfigurationController(
        ISystemConfigurationService service,
        ILogger<SystemConfigurationController> logger)
    {
        _service = service;
        _logger = logger;
    }

    /// <summary>
    /// Gets the current system configuration (SMTP + JWT settings).
    /// Creates a default row if none exists yet (fresh install).
    /// </summary>
    [HttpGet]
    [ProducesResponseType(typeof(SystemConfigurationDto), StatusCodes.Status200OK)]
    public async Task<ActionResult<SystemConfigurationDto>> Get(CancellationToken ct = default)
    {
        _logger.LogInformation("GET /api/system-configuration");
        var result = await _service.GetAsync(ct);
        return Ok(result);
    }

    /// <summary>
    /// Updates the system configuration with new SMTP and/or JWT settings.
    /// Validates input using DataAnnotation attributes on the DTO.
    /// </summary>
    [HttpPut]
    [ProducesResponseType(typeof(SystemConfigurationDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public async Task<ActionResult<SystemConfigurationDto>> Update(
        [FromBody] UpdateSystemConfigurationDto dto,
        CancellationToken ct = default)
    {
        _logger.LogInformation("PUT /api/system-configuration");

        if (!ModelState.IsValid)
            return BadRequest(ModelState);

        var result = await _service.UpdateAsync(dto, ct);
        return Ok(result);
    }

    /// <summary>
    /// Tests the currently configured Azure Blob Storage connection.
    /// Makes a lightweight GetProperties call — no blobs are created or modified.
    /// Returns 200 with { success: true } on success, or 200 with { success: false, error: "..." }
    /// when the connection fails (so the UI can show the error without treating it as an HTTP error).
    /// </summary>
    [HttpPost("test-blob-connection")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public async Task<ActionResult> TestBlobConnection(CancellationToken ct = default)
    {
        _logger.LogInformation("POST /api/system-configuration/test-blob-connection");
        var result = await _service.TestAzureBlobConnectionAsync(ct);
        // Always return 200 — the success/error information is in the response body.
        // This keeps the UI logic simple: check result.success instead of catching HTTP errors.
        return Ok(new { result.Success, result.Error });
    }
}

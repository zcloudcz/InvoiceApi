using Fakvio.Application.Service;
using Fakvio.Contracts.Dto.PaymentMatching;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Fakvio.API.Controller;

/// <summary>
/// SysAdmin-only endpoints for managing the central payment matching settings
/// (IMAP mailbox credentials, poll interval, domain).
///
/// All endpoints require the "SysAdmin" role. Regular tenant users never see this.
/// </summary>
[ApiController]
[Route("api/sysadmin/payment-matching")]
[Produces("application/json")]
[Authorize(Roles = "SysAdmin")]
public class PaymentMatchingSysAdminController : ControllerBase
{
    private readonly IPaymentMatchingSystemSettingsService _service;
    private readonly IImapPollService _pollService;
    private readonly ILogger<PaymentMatchingSysAdminController> _logger;

    public PaymentMatchingSysAdminController(
        IPaymentMatchingSystemSettingsService service,
        IImapPollService pollService,
        ILogger<PaymentMatchingSysAdminController> logger)
    {
        _service = service;
        _pollService = pollService;
        _logger = logger;
    }

    /// <summary>
    /// Returns the current system-wide payment matching configuration.
    /// The password is NEVER included in the response.
    /// </summary>
    [HttpGet("settings")]
    [ProducesResponseType(typeof(PaymentMatchingSystemSettingsDto), StatusCodes.Status200OK)]
    public async Task<ActionResult<PaymentMatchingSystemSettingsDto>> GetSettings(CancellationToken ct = default)
    {
        _logger.LogInformation("GET /api/sysadmin/payment-matching/settings");
        var dto = await _service.GetAsync(ct);
        return Ok(dto);
    }

    /// <summary>
    /// Persists new settings. Leave ImapPassword null to keep the previous password.
    /// </summary>
    [HttpPut("settings")]
    [ProducesResponseType(typeof(PaymentMatchingSystemSettingsDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public async Task<ActionResult<PaymentMatchingSystemSettingsDto>> UpdateSettings(
        [FromBody] PaymentMatchingSystemSettingsDto dto,
        CancellationToken ct = default)
    {
        _logger.LogInformation("PUT /api/sysadmin/payment-matching/settings");

        try
        {
            var result = await _service.UpdateAsync(dto, ct);
            return Ok(result);
        }
        catch (ArgumentOutOfRangeException ex)
        {
            return BadRequest(new { message = ex.Message });
        }
    }

    /// <summary>
    /// Attempts to connect to the given IMAP server with the given credentials.
    /// Does NOT save anything. Used by the "Test connection" button in the UI.
    /// </summary>
    [HttpPost("test-connection")]
    [ProducesResponseType(typeof(TestImapConnectionResult), StatusCodes.Status200OK)]
    public async Task<ActionResult<TestImapConnectionResult>> TestConnection(
        [FromBody] PaymentMatchingSystemSettingsDto dto,
        CancellationToken ct = default)
    {
        _logger.LogInformation("POST /api/sysadmin/payment-matching/test-connection host={Host}", dto.ImapHost);
        var result = await _service.TestConnectionAsync(dto, ct);
        return Ok(result);
    }

    /// <summary>
    /// Runs one IMAP poll cycle on demand — used by the SysAdmin "Run now" button.
    /// Goes through <see cref="IImapPollService"/> so it shares the advisory-lock
    /// + rate-limit semantics with the BackgroundService and the Azure Function.
    /// </summary>
    [HttpPost("run-now")]
    [ProducesResponseType(typeof(ImapPollCycleResult), StatusCodes.Status200OK)]
    public async Task<ActionResult<ImapPollCycleResult>> RunNow(CancellationToken ct = default)
    {
        _logger.LogInformation("POST /api/sysadmin/payment-matching/run-now");
        var result = await _pollService.RunCycleAsync(ct);
        return Ok(result);
    }
}

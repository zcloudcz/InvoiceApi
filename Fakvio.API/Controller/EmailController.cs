using Fakvio.Application.Service;
using Fakvio.Contracts.Dto.Email;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Fakvio.API.Controller;

/// <summary>
/// Controller for SysAdmin email operations.
/// Sends emails using system-level SMTP settings (SystemConfiguration table).
///
/// Junior note: This is NOT for sending invoices — use InvoiceController.SendInvoiceEmail for that.
/// This controller is for ad-hoc emails: notifications, announcements, support replies, etc.
/// </summary>
[ApiController]
[Route("api/[controller]")]
[Authorize(Roles = "SysAdmin")]
public class EmailController : ControllerBase
{
    private readonly IEmailService _emailService;
    private readonly ILogger<EmailController> _logger;

    public EmailController(IEmailService emailService, ILogger<EmailController> logger)
    {
        _emailService = emailService;
        _logger = logger;
    }

    /// <summary>
    /// Sends a custom email using system SMTP settings.
    /// SysAdmin only — no company context needed.
    /// </summary>
    /// <response code="200">Email sent successfully</response>
    /// <response code="400">Invalid request or SMTP not configured</response>
    /// <response code="500">SMTP error (connection, authentication, etc.)</response>
    [HttpPost("send")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public async Task<IActionResult> SendEmail(
        [FromBody] SendEmailDto dto, CancellationToken ct = default)
    {
        if (!ModelState.IsValid)
            return BadRequest(ModelState);

        try
        {
            _logger.LogInformation("SysAdmin sending email to {To}, subject: {Subject}",
                dto.To, dto.Subject);

            await _emailService.SendEmailAsync(dto.To, dto.Subject, dto.HtmlBody, ct: ct);

            _logger.LogInformation("Email sent successfully to {To}", dto.To);

            return Ok(new { message = $"Email sent to {dto.To}." });
        }
        catch (InvalidOperationException ex)
        {
            // SMTP not configured
            _logger.LogWarning(ex, "Email send failed — SMTP not configured");
            return BadRequest(new { message = $"SMTP not configured: {ex.Message}" });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Email send failed to {To}", dto.To);
            return StatusCode(StatusCodes.Status500InternalServerError,
                new { message = $"Failed to send email: {ex.Message}" });
        }
    }
}

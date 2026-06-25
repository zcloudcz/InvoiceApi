using Fakvio.Application.Service;
using Fakvio.Contracts.Dto.InvoiceEmail;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Fakvio.API.Controller;

/// <summary>
/// Manages the per-tenant invoice email mailbox ("fak-" alias).
/// One mailbox per company — activate to start receiving invoices by email.
/// </summary>
[ApiController]
[Route("api/invoice-mailbox")]
[Produces("application/json")]
[Authorize]
public class InvoiceMailboxController : ControllerBase
{
    private readonly IInvoiceMailboxService _service;
    private readonly ILogger<InvoiceMailboxController> _logger;

    public InvoiceMailboxController(
        IInvoiceMailboxService service,
        ILogger<InvoiceMailboxController> logger)
    {
        _service = service;
        _logger = logger;
    }

    /// <summary>Returns the current invoice mailbox (null if never activated).</summary>
    [HttpGet]
    [ProducesResponseType(typeof(InvoiceMailboxDto), StatusCodes.Status200OK)]
    public async Task<ActionResult<InvoiceMailboxDto?>> Get(CancellationToken ct = default)
    {
        var result = await _service.GetAsync(ct);
        return Ok(result);
    }

    /// <summary>Activates the invoice mailbox — generates a "fak-" alias.</summary>
    [HttpPost("activate")]
    [ProducesResponseType(typeof(InvoiceMailboxDto), StatusCodes.Status200OK)]
    public async Task<ActionResult<InvoiceMailboxDto>> Activate(CancellationToken ct = default)
    {
        var result = await _service.ActivateAsync(ct);
        return Ok(result);
    }

    /// <summary>Deactivates the mailbox. Emails will be ignored.</summary>
    [HttpPost("deactivate")]
    [ProducesResponseType(typeof(InvoiceMailboxDto), StatusCodes.Status200OK)]
    public async Task<ActionResult<InvoiceMailboxDto>> Deactivate(CancellationToken ct = default)
    {
        var result = await _service.DeactivateAsync(ct);
        return Ok(result);
    }

    /// <summary>Generates a new alias, retiring the old one.</summary>
    [HttpPost("regenerate")]
    [ProducesResponseType(typeof(InvoiceMailboxDto), StatusCodes.Status200OK)]
    public async Task<ActionResult<InvoiceMailboxDto>> Regenerate(CancellationToken ct = default)
    {
        var result = await _service.RegenerateAliasAsync(ct);
        return Ok(result);
    }
}

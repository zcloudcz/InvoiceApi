using Fakvio.Application.Service;
using Fakvio.Contracts.Dto.Webhook;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Fakvio.API.Controller;

/// <summary>
/// Outbound webhook subscriptions (DEVGUIDE §4.15). Admin (company owner) or SysAdmin only —
/// a webhook receives invoice data and a secret, so ordinary users and accountants must not
/// manage them. GET endpoints work with a read-only API key; POST/PUT/DELETE need write scope
/// (enforced by ApiKeyRequestGuard, see DEVGUIDE §2.9-2.10).
/// </summary>
[ApiController]
[Route("api/webhooks")]
[Produces("application/json")]
[Authorize(Roles = "Admin,SysAdmin")]
public class WebhookController : ControllerBase
{
    private readonly IWebhookSubscriptionService _service;

    public WebhookController(IWebhookSubscriptionService service)
    {
        _service = service;
    }

    /// <summary>Lists all webhook subscriptions (secrets are never included).</summary>
    [HttpGet]
    [ProducesResponseType(typeof(List<WebhookSubscriptionDto>), StatusCodes.Status200OK)]
    public async Task<ActionResult<List<WebhookSubscriptionDto>>> GetAll(CancellationToken ct)
        => Ok(await _service.GetAllAsync(ct));

    /// <summary>Gets one subscription.</summary>
    [HttpGet("{id:long}")]
    [ProducesResponseType(typeof(WebhookSubscriptionDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<WebhookSubscriptionDto>> GetById(long id, CancellationToken ct)
    {
        var result = await _service.GetByIdAsync(id, ct);
        return result == null ? NotFound() : Ok(result);
    }

    /// <summary>Creates a subscription. The response contains the signing secret — shown only this once.</summary>
    [HttpPost]
    [ProducesResponseType(typeof(WebhookSubscriptionCreatedDto), StatusCodes.Status201Created)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public async Task<ActionResult<WebhookSubscriptionCreatedDto>> Create([FromBody] CreateWebhookSubscriptionDto dto, CancellationToken ct)
    {
        try
        {
            var result = await _service.CreateAsync(dto, ct);
            return CreatedAtAction(nameof(GetById), new { id = result.Subscription.Id }, result);
        }
        catch (InvalidOperationException ex)
        {
            return BadRequest(new { message = ex.Message });
        }
    }

    /// <summary>Updates URL, description, events and the active flag.</summary>
    [HttpPut("{id:long}")]
    [ProducesResponseType(typeof(WebhookSubscriptionDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public async Task<ActionResult<WebhookSubscriptionDto>> Update(long id, [FromBody] UpdateWebhookSubscriptionDto dto, CancellationToken ct)
    {
        try
        {
            return Ok(await _service.UpdateAsync(id, dto, ct));
        }
        catch (InvalidOperationException ex)
        {
            return BadRequest(new { message = ex.Message });
        }
    }

    /// <summary>Deletes a subscription together with its delivery log.</summary>
    [HttpDelete("{id:long}")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public async Task<IActionResult> Delete(long id, CancellationToken ct)
    {
        try
        {
            await _service.DeleteAsync(id, ct);
            return NoContent();
        }
        catch (InvalidOperationException ex)
        {
            return BadRequest(new { message = ex.Message });
        }
    }

    /// <summary>Generates a new secret (the old one stops working). Returned once.</summary>
    [HttpPost("{id:long}/rotate-secret")]
    [ProducesResponseType(typeof(WebhookSubscriptionCreatedDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public async Task<ActionResult<WebhookSubscriptionCreatedDto>> RotateSecret(long id, CancellationToken ct)
    {
        try
        {
            return Ok(await _service.RotateSecretAsync(id, ct));
        }
        catch (InvalidOperationException ex)
        {
            return BadRequest(new { message = ex.Message });
        }
    }

    /// <summary>Sends a signed "ping" event synchronously and reports the endpoint's answer.</summary>
    [HttpPost("{id:long}/test")]
    [ProducesResponseType(typeof(WebhookTestResultDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public async Task<ActionResult<WebhookTestResultDto>> Test(long id, CancellationToken ct)
    {
        try
        {
            return Ok(await _service.TestAsync(id, ct));
        }
        catch (InvalidOperationException ex)
        {
            return BadRequest(new { message = ex.Message });
        }
    }

    /// <summary>Last 200 deliveries of a subscription (newest first).</summary>
    [HttpGet("{id:long}/deliveries")]
    [ProducesResponseType(typeof(List<WebhookDeliveryDto>), StatusCodes.Status200OK)]
    public async Task<ActionResult<List<WebhookDeliveryDto>>> GetDeliveries(long id, CancellationToken ct)
        => Ok(await _service.GetDeliveriesAsync(id, ct));

    /// <summary>Resets a delivery to Pending so the dispatcher sends it again (same event id).</summary>
    [HttpPost("deliveries/{deliveryId:long}/redeliver")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public async Task<IActionResult> Redeliver(long deliveryId, CancellationToken ct)
    {
        try
        {
            await _service.RedeliverAsync(deliveryId, ct);
            return NoContent();
        }
        catch (InvalidOperationException ex)
        {
            return BadRequest(new { message = ex.Message });
        }
    }
}

using Fakvio.Application.Service;
using Fakvio.Contracts.Dto.OssVatRate;
using Fakvio.Infrastructure.Service.Oss;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Fakvio.API.Controller;

/// <summary>
/// EU OSS VAT rate code table (Master DB only — see DEVGUIDE §4.15 and §11.2).
/// Reading is open to every authenticated user (the invoice item editor offers these rates);
/// writing is SysAdmin only because it is statutory reference data shared by all tenants.
/// The path is in TenantContextMiddleware.MasterOnlyPaths — no tenant context needed.
/// </summary>
[ApiController]
[Route("api/oss-vat-rate")]
[Produces("application/json")]
[Authorize]
public class OssVatRateController : ControllerBase
{
    private readonly IOssVatRateService _service;

    public OssVatRateController(IOssVatRateService service) => _service = service;

    /// <summary>Active rates of one EU country valid on <paramref name="date"/> (default today).</summary>
    [HttpGet("country/{countryCode}")]
    [ProducesResponseType(typeof(List<OssVatRateDto>), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public async Task<IActionResult> GetForCountry(string countryCode, [FromQuery] DateOnly? date, CancellationToken ct)
    {
        if (!EuCountries.IsOtherMemberState(countryCode))
            return BadRequest(new { message = $"'{countryCode}' is not an EU member state other than CZ." });

        return Ok(await _service.GetForCountryAsync(countryCode, date, ct));
    }

    /// <summary>All rates (SysAdmin overview).</summary>
    [HttpGet]
    [Authorize(Roles = "SysAdmin")]
    [ProducesResponseType(typeof(List<OssVatRateDto>), StatusCodes.Status200OK)]
    public async Task<IActionResult> GetAll([FromQuery] bool includeInactive, CancellationToken ct)
        => Ok(await _service.GetAllAsync(includeInactive, ct));

    [HttpPost]
    [Authorize(Roles = "SysAdmin")]
    [ProducesResponseType(typeof(OssVatRateDto), StatusCodes.Status201Created)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public async Task<IActionResult> Create([FromBody] CreateOssVatRateDto dto, CancellationToken ct)
    {
        try
        {
            var created = await _service.CreateAsync(dto, ct);
            return CreatedAtAction(nameof(GetForCountry), new { countryCode = created.CountryCode }, created);
        }
        catch (InvalidOperationException ex)
        {
            return BadRequest(new { message = ex.Message });
        }
    }

    [HttpPut("{id:long}")]
    [Authorize(Roles = "SysAdmin")]
    [ProducesResponseType(typeof(OssVatRateDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> Update(long id, [FromBody] UpdateOssVatRateDto dto, CancellationToken ct)
    {
        var updated = await _service.UpdateAsync(id, dto, ct);
        return updated == null ? NotFound() : Ok(updated);
    }

    /// <summary>Soft delete — historical invoices keep their rate value.</summary>
    [HttpDelete("{id:long}")]
    [Authorize(Roles = "SysAdmin")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> Deactivate(long id, CancellationToken ct)
        => await _service.DeactivateAsync(id, ct) ? NoContent() : NotFound();
}

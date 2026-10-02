using Fakvio.Application.Service;
using Fakvio.Contracts.Dto.CompanySettings;
using Fakvio.Infrastructure.Data;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace Fakvio.API.Controller;

/// <summary>
/// Tenant-scoped settings that a company Admin may change himself (unlike api/company/{id}/settings,
/// which is SysAdmin-only). The company is ALWAYS taken from the tenant resolver (JWT claim or
/// SysAdmin impersonation) — never from the route or body — and only the named fields are written.
/// The path starts with "/api/company" so it is in TenantContextMiddleware.MasterOnlyPaths
/// (it only touches the master database).
/// </summary>
[ApiController]
[Route("api/company-settings")]
[Produces("application/json")]
[Authorize(Roles = "Admin,SysAdmin")]
public class CompanySettingsController : ControllerBase
{
    private readonly MasterDbContext _master;
    private readonly ITenantResolver _tenant;

    public CompanySettingsController(MasterDbContext master, ITenantResolver tenant)
    {
        _master = master;
        _tenant = tenant;
    }

    /// <summary>Current EU OSS registration of the caller's company.</summary>
    [HttpGet("oss")]
    [ProducesResponseType(typeof(OssSettingsDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<OssSettingsDto>> GetOss(CancellationToken ct)
    {
        var settings = await FindAsync(ct);
        return settings == null ? NotFound() : Ok(ToDto(settings.OssRegistered, settings.OssRegisteredSince));
    }

    /// <summary>
    /// Saves the OSS registration. Touches ONLY OssRegistered / OssRegisteredSince — everything else on
    /// CompanySystemSettings (MaxUsers, AdminNotes, SMTP, ...) is left as is.
    /// </summary>
    [HttpPut("oss")]
    [ProducesResponseType(typeof(OssSettingsDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<OssSettingsDto>> PutOss([FromBody] OssSettingsDto dto, CancellationToken ct)
    {
        var settings = await FindAsync(ct);
        if (settings == null) return NotFound();

        settings.OssRegistered = dto.OssRegistered;
        // Normalize to a UTC date explicitly (date pickers send Unspecified kind).
        settings.OssRegisteredSince = dto.OssRegistered
            ? DateTime.SpecifyKind((dto.OssRegisteredSince ?? settings.OssRegisteredSince ?? DateTime.UtcNow).Date, DateTimeKind.Utc)
            : null;
        settings.UpdatedAt = DateTime.UtcNow;
        await _master.SaveChangesAsync(ct);

        return Ok(ToDto(settings.OssRegistered, settings.OssRegisteredSince));
    }

    private async Task<Domain.Entities.CompanySystemSettings?> FindAsync(CancellationToken ct)
    {
        var companyId = _tenant.GetCurrentCompanyId();
        return companyId == null
            ? null
            : await _master.CompanySystemSettings.FirstOrDefaultAsync(s => s.CompanyId == companyId, ct);
    }

    private static OssSettingsDto ToDto(bool registered, DateTime? since) =>
        new() { OssRegistered = registered, OssRegisteredSince = since };
}

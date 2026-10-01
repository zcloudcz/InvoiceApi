using Fakvio.Application.Service;
using Fakvio.Contracts.Dto.CompanyMembership;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Fakvio.API.Controller;

/// <summary>Platform administrators manage each membership independently of account defaults.</summary>
[ApiController, Authorize(Roles = "SysAdmin"), Route("api/user/{userId:long}/memberships"), FeedbackErrors]
public sealed class UserCompanyMembershipController(ICompanyMembershipService memberships) : ControllerBase
{
    [HttpGet]
    public async Task<IActionResult> List(long userId, CancellationToken ct)
    {
        try { return Ok(await memberships.ListForUserAsync(userId, ct)); }
        catch (KeyNotFoundException) { return NotFound(); }
    }

    [HttpPut("{companyId:long}")]
    public async Task<IActionResult> Update(long userId, long companyId, UpdateCompanyMembershipDto input, CancellationToken ct)
    {
        try { return Ok(await memberships.UpdateForUserAsync(userId, companyId, input, ct)); }
        catch (KeyNotFoundException) { return NotFound(); }
    }
}

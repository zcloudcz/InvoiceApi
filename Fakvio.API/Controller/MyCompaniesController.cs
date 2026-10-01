using System.Net;
using Fakvio.Application.Service;
using Fakvio.Contracts.Dto.CompanyMembership;
using Fakvio.Contracts.Dto.Auth;
using Fakvio.Infrastructure.Service;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Fakvio.API.Controller;

/// <summary>Identity-owned company access. Services verify persisted identity and membership on every operation.</summary>
[ApiController, Authorize, Route("api/my-companies"), FeedbackErrors]
public sealed class MyCompaniesController(ICompanyMembershipService memberships, CompanySessionService sessions,
    IEmailService email, ISystemConfigurationService systemConfiguration, IConfiguration configuration,
    ILogger<MyCompaniesController> logger) : ControllerBase
{
    [HttpGet]
    public async Task<IActionResult> List(CancellationToken ct) => Ok(await memberships.ListAsync(ct));

    [HttpPost]
    public async Task<IActionResult> Create(CreateMyCompanyDto input, CancellationToken ct) => Ok(await memberships.CreateAsync(input, ct));

    [HttpPost("{companyId:long}/retry-provisioning")]
    public async Task<IActionResult> Retry(long companyId, CancellationToken ct) => Ok(await memberships.RetryProvisioningAsync(companyId, ct));

    [HttpPost("switch")]
    public async Task<ActionResult<LoginResponse>> Switch(SwitchCompanyDto input, CancellationToken ct)
        => Ok(await sessions.CreateAsync(input.CompanyId, ct));

    [HttpPost("invitations"), Authorize(Roles = "SysAdmin")]
    public async Task<IActionResult> Invite(InviteCompanyMemberDto input, CancellationToken ct)
    {
        var invitation = await memberships.InviteAsync(input, ct);
        try
        {
            var settings = await systemConfiguration.GetAsync(ct);
            var baseUrl = !string.IsNullOrWhiteSpace(settings.BlazorBaseUrl) ? settings.BlazorBaseUrl : configuration["AppSettings:BlazorBaseUrl"] ?? "https://app.fakvio.cz";
            // Fragment keeps the one-use secret out of web-server query logs.
            var link = $"{baseUrl.TrimEnd('/')}/company-invitations/accept#{Uri.EscapeDataString(invitation.Token!)}";
            await email.SendEmailAsync(invitation.Email, "Company invitation / Pozvánka do firmy — Fakvio",
                $"<p>You were invited to / Byli jste pozváni do {WebUtility.HtmlEncode(invitation.CompanyName)}.</p><p><a href=\"{WebUtility.HtmlEncode(link)}\">Accept invitation / Přijmout pozvánku</a></p><p>Sign in with your existing account. Your password will not change. / Přihlaste se svým stávajícím účtem. Heslo se nezmění.</p>", ct: ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception ex)
        {
            // The administrator receives the one-time link for manual delivery even if SMTP fails.
            logger.LogWarning("Company invitation {InvitationId} email failed ({ErrorType})", invitation.Id, ex.GetType().Name);
        }
        return Ok(invitation);
    }

    [HttpPost("invitations/accept")]
    public async Task<IActionResult> Accept(AcceptCompanyInvitationDto input, CancellationToken ct)
        => Ok(await memberships.AcceptAsync(input.Token, ct));
}

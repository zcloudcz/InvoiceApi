using System.Security.Claims;
using Fakvio.Domain.Enums;
using Fakvio.Infrastructure.Authentication;
using Fakvio.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;

namespace Fakvio.API.Middleware;

/// <summary>Revalidates persisted identity and selected membership before endpoint authorization.</summary>
public sealed class CompanyMembershipMiddleware(RequestDelegate next)
{
    public async Task InvokeAsync(HttpContext context, MasterDbContext db)
    {
        if (context.User.Identity?.IsAuthenticated != true) { await next(context); return; }
        var ct = context.RequestAborted;
        if (!long.TryParse(context.User.FindFirstValue(ClaimTypes.NameIdentifier), out var userId))
        { await Deny(context); return; }
        var user = await db.User.AsNoTracking().Where(u => u.Id == userId && u.IsActive)
            .Select(u => new { u.Id, u.Role }).SingleOrDefaultAsync(ct);
        if (user is null) { await Deny(context); return; }
        var identity = (ClaimsIdentity)context.User.Identity;
        var scoped = context.User.HasClaim(c => c.Type == ApiKeyAuthenticationDefaults.ScopeClaimType);
        var oauth = context.User.HasClaim(c => c.Type == ApiKeyAuthenticationDefaults.OAuthGrantIdClaimType);
        var header = context.Request.Headers["X-Selected-Company-Id"].ToString();
        long? companyId = long.TryParse(identity.FindFirst("CompanyId")?.Value, out var selected) ? selected : null;
        if (header.Length > 0)
        {
            if (!scoped || oauth || context.Request.Headers.ContainsKey("X-Company-Id") ||
                !long.TryParse(header, out var requested) || requested <= 0)
            { await Deny(context); return; }
            if (!long.TryParse(context.User.FindFirstValue(ApiKeyAuthenticationDefaults.KeyIdClaimType), out var keyId))
            { await Deny(context); return; }
            var grants = await db.ApiKey.Where(k => k.Id == keyId && k.UserId == userId)
                .Select(k => k.AllowedCompanyIds).SingleOrDefaultAsync(ct);
            if (grants is null || !grants.Contains(requested)) { await Deny(context); return; }
            companyId = requested;
        }
        var path = context.Request.Path.Value?.TrimEnd('/').ToLowerInvariant() ?? "";
        if (scoped && (path == "/api/auth/refresh" || path == "/api/my-companies/switch"
            || path == "/api/my-companies/invitations/accept" || ((oauth || user.Role != EUserRole.SysAdmin) && context.Request.Headers.ContainsKey("X-Company-Id"))))
        { await Deny(context); return; }
        if (user.Role == EUserRole.SysAdmin)
        {
            Replace(identity, ClaimTypes.Role, EUserRole.SysAdmin.ToString());
            Replace(identity, "CompanyId", companyId?.ToString());
            await next(context); return;
        }
        var membership = companyId.HasValue ? await db.UserCompanyMembership.AsNoTracking()
            .Where(m => m.UserId == userId && m.CompanyId == companyId && m.IsActive && m.Company.IsActive && m.Company.IsIssuer)
            .Select(m => new { m.Role }).SingleOrDefaultAsync(ct) : null;
        if (membership is null || membership.Role is not (EUserRole.User or EUserRole.Admin))
        {
            // Recovery endpoints have their own object checks; no former-company access is granted.
            var recovery = path == "/api/api-key/me" || path == "/api/my-companies" || path == "/api/my-companies/switch"
                || path == "/api/my-companies/invitations/accept" || path == "/api/auth/logout"
                || path.StartsWith("/api/my-companies/") && path.EndsWith("/retry-provisioning");
            if (!recovery) { await Deny(context); return; }
            Replace(identity, ClaimTypes.Role, EUserRole.User.ToString());
            Replace(identity, "CompanyId", null);
        }
        else
        {
            Replace(identity, ClaimTypes.Role, membership.Role.ToString());
            Replace(identity, "CompanyId", companyId!.Value.ToString());
        }
        await next(context);
    }
    private static void Replace(ClaimsIdentity identity, string type, string? value)
    {
        foreach (var claim in identity.FindAll(type).ToList()) identity.RemoveClaim(claim);
        if (value is not null) identity.AddClaim(new Claim(type, value));
    }
    private static Task Deny(HttpContext context)
    {
        context.Response.StatusCode = StatusCodes.Status403Forbidden;
        return context.Response.WriteAsJsonAsync(new { message = "Current company access is not authorized." }, context.RequestAborted);
    }
}

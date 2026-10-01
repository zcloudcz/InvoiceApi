using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Text;
using Fakvio.Application.Service;
using Fakvio.Contracts.Dto.Auth;
using Fakvio.Domain.Enums;
using Fakvio.Infrastructure.Authentication;
using Fakvio.Infrastructure.Data;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.IdentityModel.Tokens;
namespace Fakvio.Infrastructure.Service;

/// <summary>Issues a selected-company browser session using the established JWT signing and lifetime settings.</summary>
public sealed class CompanySessionService(MasterDbContext db, ICurrentUserService currentUser, IHttpContextAccessor http, IConfiguration configuration)
{
    public async Task<LoginResponse> CreateAsync(long? companyId, CancellationToken ct = default)
    {
        if (http.HttpContext?.User.HasClaim(c => c.Type == ApiKeyAuthenticationDefaults.ScopeClaimType) == true)
            throw new UnauthorizedAccessException("An interactive session is required.");
        var user = await db.User.AsNoTracking().SingleOrDefaultAsync(u => u.Id == currentUser.GetCurrentUserId() && u.IsActive, ct)
            ?? throw new UnauthorizedAccessException();
        var role = user.Role;
        string? companyName = null;
        if (user.Role != EUserRole.SysAdmin)
        {
            var membership = await db.UserCompanyMembership.AsNoTracking()
                .Where(m => m.UserId == user.Id && m.CompanyId == companyId && m.IsActive && m.Company.IsActive && m.Company.IsIssuer)
                .Select(m => new { m.Role, m.Company.CompanyName }).SingleOrDefaultAsync(ct);
            if (membership is null || membership.Role is not (EUserRole.User or EUserRole.Admin))
                throw new UnauthorizedAccessException("Company membership is not active.");
            role = membership.Role; companyName = membership.CompanyName;
        }
        if (companyId.HasValue)
        {
            if (!await db.CompanySystemSettings.AnyAsync(s => s.CompanyId == companyId && s.IsActive && s.IsProvisioned, ct))
                throw new UnauthorizedAccessException("Company workspace is not ready.");
            companyName ??= await db.Client.Where(c => c.Id == companyId && c.IsIssuer && c.IsActive).Select(c => c.CompanyName).SingleOrDefaultAsync(ct)
                ?? throw new UnauthorizedAccessException();
        }
        var claims = new List<Claim> { new(ClaimTypes.NameIdentifier, user.Id.ToString()), new(ClaimTypes.Email, user.Email),
            new(ClaimTypes.Name, user.FullName), new(ClaimTypes.Role, role.ToString()) };
        if (companyId.HasValue) claims.Add(new("CompanyId", companyId.Value.ToString()));
        var secret = configuration["JwtSettings:Secret"] ?? throw new InvalidOperationException("JWT Secret is not configured.");
        var expires = DateTime.UtcNow.AddHours(int.TryParse(configuration["JwtSettings:ExpirationHours"], out var hours) ? hours : 24);
        var jwt = new JwtSecurityToken(configuration["JwtSettings:Issuer"] ?? "Fakvio", configuration["JwtSettings:Audience"] ?? "FakvioClient",
            claims, expires: expires, signingCredentials: new SigningCredentials(new SymmetricSecurityKey(Encoding.UTF8.GetBytes(secret)), SecurityAlgorithms.HmacSha256));
        return new LoginResponse { Token = new JwtSecurityTokenHandler().WriteToken(jwt), ExpiresAt = expires, UserId = user.Id,
            Email = user.Email, FullName = user.FullName, Role = role, CompanyId = companyId, CompanyName = companyName, IsExternalLogin = user.IsExternalLogin };
    }
}

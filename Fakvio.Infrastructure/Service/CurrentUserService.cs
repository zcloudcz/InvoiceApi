using System.Security.Claims;
using Fakvio.Application.Service;
using Microsoft.AspNetCore.Http;

namespace Fakvio.Infrastructure.Service;

/// <summary>
/// Reads the currently authenticated user's ID from the JWT claims in HttpContext.
/// This service is injected into MasterDbContext/TenantDbContext to automatically populate
/// CreatedByUserId and UpdatedByUserId audit fields on every SaveChanges call.
///
/// Returns null when:
/// - No HttpContext exists (e.g., background jobs, console tools)
/// - The user is not authenticated (anonymous endpoints)
/// - The NameIdentifier claim is missing or not a valid long
/// </summary>
public class CurrentUserService : ICurrentUserService
{
    private readonly IHttpContextAccessor _httpContextAccessor;

    public CurrentUserService(IHttpContextAccessor httpContextAccessor)
    {
        _httpContextAccessor = httpContextAccessor;
    }

    /// <inheritdoc />
    public long? GetCurrentUserId()
    {
        // Read the NameIdentifier claim from the JWT token.
        // This is set during authentication in AuthService.GenerateJwtTokenAsync.
        var userIdClaim = _httpContextAccessor.HttpContext?.User
            ?.FindFirst(ClaimTypes.NameIdentifier)?.Value;

        // Parse to long — return null if claim is missing or not a valid number
        if (long.TryParse(userIdClaim, out var userId))
        {
            return userId;
        }

        return null;
    }
}

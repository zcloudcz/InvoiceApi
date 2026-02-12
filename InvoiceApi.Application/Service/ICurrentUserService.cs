namespace InvoiceApi.Application.Service;

/// <summary>
/// Provides access to the currently authenticated user's identity.
/// Used by MasterDbContext/TenantDbContext to auto-fill audit fields (CreatedByUserId, UpdatedByUserId)
/// on every SaveChanges call, so services don't need to set them manually.
///
/// Implementation reads the UserId from the JWT claims in HttpContext.
/// Returns null when no user is authenticated (e.g., during migrations or seed data).
/// </summary>
public interface ICurrentUserService
{
    /// <summary>
    /// Gets the ID of the currently authenticated user from the JWT token.
    /// Returns null if no user is authenticated (anonymous requests, background jobs, migrations).
    /// </summary>
    long? GetCurrentUserId();
}

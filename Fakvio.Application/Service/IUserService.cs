using Fakvio.Contracts.Common.Pagination;
using Fakvio.Contracts.Dto.User;
using Fakvio.Domain.Enums;

namespace Fakvio.Application.Service;

/// <summary>
/// Service for user management operations
/// Handles CRUD operations for users
/// </summary>
public interface IUserService
{
    /// <summary>
    /// Gets all users (optionally filtered by company)
    /// </summary>
    /// <param name="companyId">Filter by company ID (null for all companies - SysAdmin only)</param>
    /// <param name="includeInactive">Include inactive users</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>List of users</returns>
    Task<List<UserDto>> GetAllUsersAsync(long? companyId = null, bool includeInactive = false, CancellationToken cancellationToken = default);

    /// <summary>
    /// Gets paginated, filtered and sorted users
    /// </summary>
    Task<PagedResult<UserDto>> GetUsersPagedAsync(
        UserFilterDto filter,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Gets a specific user by ID
    /// </summary>
    /// <param name="userId">User ID</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>User data or null if not found</returns>
    Task<UserDto?> GetUserByIdAsync(long userId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Gets user by email
    /// </summary>
    /// <param name="email">User email</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>User data or null if not found</returns>
    Task<UserDto?> GetUserByEmailAsync(string email, CancellationToken cancellationToken = default);

    /// <summary>
    /// Creates a new user
    /// </summary>
    /// <param name="createDto">User creation data</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>Created user</returns>
    Task<UserDto> CreateUserAsync(CreateUserDto createDto, CancellationToken cancellationToken = default);

    /// <summary>
    /// Updates an existing user
    /// </summary>
    /// <param name="userId">User ID to update</param>
    /// <param name="updateDto">Updated data</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>Updated user or null if not found</returns>
    Task<UserDto?> UpdateUserAsync(long userId, UpdateUserDto updateDto, CancellationToken cancellationToken = default);

    /// <summary>
    /// Changes user password (user must provide their current password)
    /// </summary>
    /// <param name="userId">User ID</param>
    /// <param name="changePasswordDto">Password change data</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>True if password changed successfully</returns>
    Task<bool> ChangePasswordAsync(long userId, ChangePasswordDto changePasswordDto, CancellationToken cancellationToken = default);

    /// <summary>
    /// Resets a user's password (Admin/SysAdmin operation — no current password required).
    /// Used by administrators who need to reset a user's password without knowing it.
    /// </summary>
    /// <param name="userId">User ID whose password to reset</param>
    /// <param name="newPassword">The new password to set</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>True if password was reset, false if user not found</returns>
    Task<bool> AdminResetPasswordAsync(long userId, string newPassword, CancellationToken cancellationToken = default);

    /// <summary>
    /// Deletes a user (soft delete - sets IsActive = false)
    /// </summary>
    /// <param name="userId">User ID to delete</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>True if deleted, false if not found</returns>
    Task<bool> DeleteUserAsync(long userId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Invites a new user by creating their account with a random temporary password
    /// and generating a unique invitation token. The user will set their own password
    /// by clicking the invitation link sent via email.
    /// </summary>
    /// <param name="dto">Invitation data (email, name, role, company)</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>Created user DTO</returns>
    Task<UserDto> InviteUserAsync(InviteUserDto dto, CancellationToken cancellationToken = default);

    /// <summary>
    /// Sets the password for an invited user using their invitation token.
    /// Validates the token, checks expiration, hashes the new password,
    /// and clears the invitation fields.
    /// </summary>
    /// <param name="dto">Token and new password</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>True if password was set successfully, false if token is invalid/expired</returns>
    Task<bool> SetPasswordAsync(SetPasswordDto dto, CancellationToken cancellationToken = default);

    /// <summary>
    /// Validates whether an invitation token is valid and not expired.
    /// Used by the frontend to check before showing the password form.
    /// </summary>
    /// <param name="token">The invitation token (GUID string)</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>True if the token is valid and not expired</returns>
    Task<bool> ValidateInvitationTokenAsync(string token, CancellationToken cancellationToken = default);
}

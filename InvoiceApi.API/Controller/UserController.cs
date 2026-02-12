using InvoiceApi.Application.Common.Pagination;
using InvoiceApi.Application.Dto.User;
using InvoiceApi.Application.Service;
using InvoiceApi.Domain.Enums;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Configuration;

namespace InvoiceApi.API.Controller;

/// <summary>
/// Controller for user management operations
/// Handles CRUD operations for users with role-based authorization
/// </summary>
[ApiController]
[Route("api/[controller]")]
[Authorize] // All endpoints require authentication
public class UserController : ControllerBase
{
    private readonly IUserService _userService;
    private readonly IEmailService _emailService;
    private readonly IConfiguration _configuration;
    private readonly ILogger<UserController> _logger;

    public UserController(
        IUserService userService,
        IEmailService emailService,
        IConfiguration configuration,
        ILogger<UserController> logger)
    {
        _userService = userService;
        _emailService = emailService;
        _configuration = configuration;
        _logger = logger;
    }

    /// <summary>
    /// Gets all users
    /// SysAdmin can see all users, Admin/User only see users from their company
    /// </summary>
    /// <param name="companyId">Filter by company ID (SysAdmin only)</param>
    /// <param name="includeInactive">Include inactive users</param>
    /// <returns>List of users</returns>
    /// <response code="200">Returns list of users</response>
    /// <response code="403">User not authorized to view users from other companies</response>
    [HttpGet]
    [ProducesResponseType(typeof(List<UserDto>), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    public async Task<ActionResult<List<UserDto>>> GetAllUsers(
        [FromQuery] long? companyId = null,
        [FromQuery] bool includeInactive = false)
    {
        try
        {
            var currentUserRole = GetCurrentUserRole();
            var currentUserCompanyId = GetCurrentUserCompanyId();

            // SysAdmin can view any company, others can only view their own company
            if (currentUserRole != EUserRole.SysAdmin)
            {
                // Non-SysAdmin trying to access different company
                if (companyId.HasValue && companyId.Value != currentUserCompanyId)
                {
                    return Forbid();
                }

                // Force companyId to current user's company
                companyId = currentUserCompanyId;
            }

            var users = await _userService.GetAllUsersAsync(companyId, includeInactive);
            return Ok(users);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error retrieving users");
            return StatusCode(StatusCodes.Status500InternalServerError, new { message = "An error occurred while retrieving users." });
        }
    }

    /// <summary>
    /// Gets paginated, filtered and sorted users
    /// Supports pagination, filtering by multiple criteria, and sorting
    /// </summary>
    /// <param name="filter">Filter parameters including pagination, search, and sorting</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>Paged result of users</returns>
    /// <response code="200">Returns paged list of users</response>
    /// <response code="403">User not authorized to view users from other companies</response>
    [HttpGet("paged")]
    [ProducesResponseType(typeof(PagedResult<UserDto>), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    public async Task<ActionResult<PagedResult<UserDto>>> GetUsersPaged(
        [FromQuery] UserFilterDto filter,
        CancellationToken cancellationToken = default)
    {
        try
        {
            _logger.LogInformation("GET /api/user/paged - Page: {Page}, PageSize: {PageSize}, Search: {Search}",
                filter.Page, filter.PageSize, filter.Search);

            var currentUserRole = GetCurrentUserRole();
            var currentUserCompanyId = GetCurrentUserCompanyId();

            // SysAdmin can view any company, others can only view their own company
            if (currentUserRole != EUserRole.SysAdmin)
            {
                // Non-SysAdmin trying to access different company
                if (filter.CompanyId.HasValue && filter.CompanyId.Value != currentUserCompanyId)
                {
                    return Forbid();
                }

                // Force companyId to current user's company
                filter.CompanyId = currentUserCompanyId;
            }

            var result = await _userService.GetUsersPagedAsync(filter, cancellationToken);

            return Ok(result);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error retrieving paged users");
            return StatusCode(StatusCodes.Status500InternalServerError,
                new { message = "An error occurred while retrieving users." });
        }
    }

    /// <summary>
    /// Gets a specific user by ID
    /// </summary>
    /// <param name="id">User ID</param>
    /// <returns>User data</returns>
    /// <response code="200">Returns user data</response>
    /// <response code="403">User not authorized to view this user</response>
    /// <response code="404">User not found</response>
    [HttpGet("{id}")]
    [ProducesResponseType(typeof(UserDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<UserDto>> GetUserById(long id)
    {
        try
        {
            var user = await _userService.GetUserByIdAsync(id);

            if (user == null)
            {
                return NotFound(new { message = $"User with ID {id} not found." });
            }

            // Check authorization - users can only view users from their company (except SysAdmin)
            if (!CanAccessUser(user.CompanyId))
            {
                return Forbid();
            }

            return Ok(user);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error retrieving user {UserId}", id);
            return StatusCode(StatusCodes.Status500InternalServerError, new { message = "An error occurred while retrieving the user." });
        }
    }

    /// <summary>
    /// Creates a new user
    /// Admin can create users in their company, SysAdmin can create users in any company
    /// </summary>
    /// <param name="createDto">User creation data</param>
    /// <returns>Created user</returns>
    /// <response code="201">User created successfully</response>
    /// <response code="400">Invalid data or email already exists</response>
    /// <response code="403">Not authorized to create users</response>
    [HttpPost]
    [Authorize(Roles = "Admin,SysAdmin")] // Only Admin and SysAdmin can create users
    [ProducesResponseType(typeof(UserDto), StatusCodes.Status201Created)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    public async Task<ActionResult<UserDto>> CreateUser([FromBody] CreateUserDto createDto)
    {
        try
        {
            var currentUserRole = GetCurrentUserRole();
            var currentUserCompanyId = GetCurrentUserCompanyId();

            // Non-SysAdmin can only create users in their own company
            if (currentUserRole != EUserRole.SysAdmin)
            {
                if (createDto.CompanyId != currentUserCompanyId)
                {
                    return Forbid();
                }

                // Admin cannot create SysAdmin users
                if (createDto.Role == EUserRole.SysAdmin)
                {
                    return BadRequest(new { message = "Only SysAdmin can create SysAdmin users." });
                }
            }

            var user = await _userService.CreateUserAsync(createDto);

            return CreatedAtAction(nameof(GetUserById), new { id = user.Id }, user);
        }
        catch (InvalidOperationException ex)
        {
            _logger.LogWarning(ex, "Failed to create user: {Email}", createDto.Email);
            return BadRequest(new { message = ex.Message });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error creating user: {Email}", createDto.Email);
            return StatusCode(StatusCodes.Status500InternalServerError, new { message = "An error occurred while creating the user." });
        }
    }

    /// <summary>
    /// Updates an existing user
    /// Admin can update users in their company, SysAdmin can update any user
    /// </summary>
    /// <param name="id">User ID to update</param>
    /// <param name="updateDto">Updated user data</param>
    /// <returns>Updated user</returns>
    /// <response code="200">User updated successfully</response>
    /// <response code="400">Invalid data</response>
    /// <response code="403">Not authorized to update this user</response>
    /// <response code="404">User not found</response>
    [HttpPut("{id}")]
    [Authorize(Roles = "Admin,SysAdmin")]
    [ProducesResponseType(typeof(UserDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<UserDto>> UpdateUser(long id, [FromBody] UpdateUserDto updateDto)
    {
        try
        {
            // Check if user exists first
            var existingUser = await _userService.GetUserByIdAsync(id);
            if (existingUser == null)
            {
                return NotFound(new { message = $"User with ID {id} not found." });
            }

            // Check authorization
            if (!CanAccessUser(existingUser.CompanyId))
            {
                return Forbid();
            }

            var currentUserRole = GetCurrentUserRole();
            var currentUserCompanyId = GetCurrentUserCompanyId();

            // Non-SysAdmin cannot change user to different company
            if (currentUserRole != EUserRole.SysAdmin && updateDto.CompanyId.HasValue)
            {
                if (updateDto.CompanyId.Value != currentUserCompanyId)
                {
                    return BadRequest(new { message = "Cannot move user to different company." });
                }
            }

            // Admin cannot set role to SysAdmin
            if (currentUserRole != EUserRole.SysAdmin && updateDto.Role == EUserRole.SysAdmin)
            {
                return BadRequest(new { message = "Only SysAdmin can assign SysAdmin role." });
            }

            var user = await _userService.UpdateUserAsync(id, updateDto);

            return Ok(user);
        }
        catch (InvalidOperationException ex)
        {
            _logger.LogWarning(ex, "Failed to update user {UserId}", id);
            return BadRequest(new { message = ex.Message });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error updating user {UserId}", id);
            return StatusCode(StatusCodes.Status500InternalServerError, new { message = "An error occurred while updating the user." });
        }
    }

    /// <summary>
    /// Changes user password
    /// Users can change their own password, Admin can change passwords for users in their company
    /// </summary>
    /// <param name="id">User ID</param>
    /// <param name="changePasswordDto">Password change data</param>
    /// <returns>Success status</returns>
    /// <response code="200">Password changed successfully</response>
    /// <response code="400">Invalid current password</response>
    /// <response code="403">Not authorized to change this user's password</response>
    /// <response code="404">User not found</response>
    [HttpPost("{id}/change-password")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult> ChangePassword(long id, [FromBody] ChangePasswordDto changePasswordDto)
    {
        try
        {
            var currentUserId = GetCurrentUserId();
            var currentUserRole = GetCurrentUserRole();

            // Get target user
            var targetUser = await _userService.GetUserByIdAsync(id);
            if (targetUser == null)
            {
                return NotFound(new { message = $"User with ID {id} not found." });
            }

            // Users can change their own password, or Admin/SysAdmin can change passwords for users they manage
            if (currentUserId != id)
            {
                if (currentUserRole == EUserRole.User)
                {
                    return Forbid();
                }

                if (!CanAccessUser(targetUser.CompanyId))
                {
                    return Forbid();
                }
            }

            var result = await _userService.ChangePasswordAsync(id, changePasswordDto);

            if (!result)
            {
                return NotFound(new { message = $"User with ID {id} not found." });
            }

            return Ok(new { message = "Password changed successfully." });
        }
        catch (InvalidOperationException ex)
        {
            _logger.LogWarning(ex, "Failed to change password for user {UserId}", id);
            return BadRequest(new { message = ex.Message });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error changing password for user {UserId}", id);
            return StatusCode(StatusCodes.Status500InternalServerError, new { message = "An error occurred while changing the password." });
        }
    }

    /// <summary>
    /// Resets a user's password without requiring the current password.
    /// Only Admin and SysAdmin can perform this operation.
    /// Admin can only reset passwords for users in their company.
    /// </summary>
    /// <param name="id">User ID</param>
    /// <param name="dto">New password data</param>
    /// <returns>Success status</returns>
    /// <response code="200">Password reset successfully</response>
    /// <response code="403">Not authorized to reset this user's password</response>
    /// <response code="404">User not found</response>
    [HttpPost("{id}/admin-reset-password")]
    [Authorize(Roles = "Admin,SysAdmin")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult> AdminResetPassword(long id, [FromBody] AdminResetPasswordDto dto)
    {
        try
        {
            // Check if target user exists
            var targetUser = await _userService.GetUserByIdAsync(id);
            if (targetUser == null)
            {
                return NotFound(new { message = $"User with ID {id} not found." });
            }

            // Check authorization — Admin can only reset passwords for users in their company
            if (!CanAccessUser(targetUser.CompanyId))
            {
                return Forbid();
            }

            var result = await _userService.AdminResetPasswordAsync(id, dto.NewPassword);

            if (!result)
            {
                return NotFound(new { message = $"User with ID {id} not found." });
            }

            return Ok(new { message = "Password reset successfully." });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error resetting password for user {UserId}", id);
            return StatusCode(StatusCodes.Status500InternalServerError,
                new { message = "An error occurred while resetting the password." });
        }
    }

    /// <summary>
    /// Deletes a user (soft delete - sets IsActive = false)
    /// Admin can delete users in their company, SysAdmin can delete any user
    /// </summary>
    /// <param name="id">User ID to delete</param>
    /// <returns>Success status</returns>
    /// <response code="200">User deleted successfully</response>
    /// <response code="403">Not authorized to delete this user</response>
    /// <response code="404">User not found</response>
    [HttpDelete("{id}")]
    [Authorize(Roles = "Admin,SysAdmin")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult> DeleteUser(long id)
    {
        try
        {
            // Check if user exists first
            var existingUser = await _userService.GetUserByIdAsync(id);
            if (existingUser == null)
            {
                return NotFound(new { message = $"User with ID {id} not found." });
            }

            // Check authorization
            if (!CanAccessUser(existingUser.CompanyId))
            {
                return Forbid();
            }

            var result = await _userService.DeleteUserAsync(id);

            if (!result)
            {
                return NotFound(new { message = $"User with ID {id} not found." });
            }

            return Ok(new { message = "User deleted successfully." });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error deleting user {UserId}", id);
            return StatusCode(StatusCodes.Status500InternalServerError, new { message = "An error occurred while deleting the user." });
        }
    }

    /// <summary>
    /// Invites a new user by creating their account and sending an invitation email.
    /// The user will receive an email with a link to set their password.
    /// Only Admin and SysAdmin can invite users.
    /// </summary>
    /// <param name="inviteDto">Invitation data (email, name, role, company)</param>
    /// <returns>Created user data</returns>
    /// <response code="201">User invited successfully</response>
    /// <response code="400">Invalid data or email already exists</response>
    /// <response code="403">Not authorized to invite users</response>
    [HttpPost("invite")]
    [Authorize(Roles = "Admin,SysAdmin")]
    [ProducesResponseType(typeof(UserDto), StatusCodes.Status201Created)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    public async Task<ActionResult<UserDto>> InviteUser([FromBody] InviteUserDto inviteDto)
    {
        try
        {
            var currentUserRole = GetCurrentUserRole();
            var currentUserCompanyId = GetCurrentUserCompanyId();

            // Non-SysAdmin can only invite users to their own company
            if (currentUserRole != EUserRole.SysAdmin)
            {
                if (inviteDto.CompanyId != currentUserCompanyId)
                {
                    return Forbid();
                }

                // Admin cannot create SysAdmin users
                if (inviteDto.Role == EUserRole.SysAdmin)
                {
                    return BadRequest(new { message = "Only SysAdmin can invite SysAdmin users." });
                }
            }

            // Create the user with invitation token
            var user = await _userService.InviteUserAsync(inviteDto);

            // Build the invitation link pointing to the Blazor UI set-password page
            // Reads the base URL from configuration (AppSettings:BlazorBaseUrl)
            var blazorBaseUrl = _configuration["AppSettings:BlazorBaseUrl"]?.TrimEnd('/')
                ?? "https://localhost:5002";

            // The invitation token is included in the UserDto returned by InviteUserAsync
            var invitationLink = $"{blazorBaseUrl}/set-password?token={Uri.EscapeDataString(user.InvitationToken ?? "")}";

            // Send the invitation email
            try
            {
                await _emailService.SendInvitationEmailAsync(
                    inviteDto.Email,
                    $"{inviteDto.FirstName} {inviteDto.LastName}",
                    invitationLink);
            }
            catch (Exception emailEx)
            {
                // Log email failure but still return success — the user was created
                _logger.LogWarning(emailEx, "Failed to send invitation email to {Email}. User was created but email not sent.", inviteDto.Email);
            }

            return CreatedAtAction(nameof(GetUserById), new { id = user.Id }, user);
        }
        catch (InvalidOperationException ex)
        {
            _logger.LogWarning(ex, "Failed to invite user: {Email}", inviteDto.Email);
            return BadRequest(new { message = ex.Message });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error inviting user: {Email}", inviteDto.Email);
            return StatusCode(StatusCodes.Status500InternalServerError,
                new { message = "An error occurred while inviting the user." });
        }
    }

    /// <summary>
    /// Sets password for an invited user using their invitation token.
    /// This endpoint is anonymous — authentication is done via the token itself.
    /// </summary>
    /// <param name="dto">Token and new password</param>
    /// <returns>Success or failure status</returns>
    /// <response code="200">Password set successfully</response>
    /// <response code="400">Invalid or expired token</response>
    [HttpPost("set-password")]
    [AllowAnonymous]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public async Task<ActionResult> SetPassword([FromBody] SetPasswordDto dto)
    {
        try
        {
            var result = await _userService.SetPasswordAsync(dto);

            if (!result)
            {
                return BadRequest(new { message = "Invalid or expired invitation token." });
            }

            return Ok(new { message = "Password set successfully. You can now log in." });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error setting password via invitation token");
            return StatusCode(StatusCodes.Status500InternalServerError,
                new { message = "An error occurred while setting the password." });
        }
    }

    /// <summary>
    /// Validates an invitation token — checks if it exists and is not expired.
    /// Used by the frontend to verify the token before showing the password form.
    /// This endpoint is anonymous — no authentication needed.
    /// </summary>
    /// <param name="token">The invitation token (GUID string)</param>
    /// <returns>True if token is valid</returns>
    /// <response code="200">Token validation result</response>
    [HttpGet("validate-invitation")]
    [AllowAnonymous]
    [ProducesResponseType(typeof(object), StatusCodes.Status200OK)]
    public async Task<ActionResult> ValidateInvitationToken([FromQuery] string token)
    {
        var isValid = await _userService.ValidateInvitationTokenAsync(token);
        return Ok(new { isValid });
    }

    #region Helper Methods

    /// <summary>
    /// Gets current user's ID from JWT claims
    /// </summary>
    private long GetCurrentUserId()
    {
        var userIdClaim = User.FindFirst(System.Security.Claims.ClaimTypes.NameIdentifier)?.Value;
        return long.TryParse(userIdClaim, out var userId) ? userId : 0;
    }

    /// <summary>
    /// Gets current user's role from JWT claims
    /// </summary>
    private EUserRole GetCurrentUserRole()
    {
        var roleClaim = User.FindFirst(System.Security.Claims.ClaimTypes.Role)?.Value;
        return Enum.TryParse<EUserRole>(roleClaim, out var role) ? role : EUserRole.User;
    }

    /// <summary>
    /// Gets current user's company ID from JWT claims
    /// Returns null for SysAdmin
    /// </summary>
    private long? GetCurrentUserCompanyId()
    {
        var companyIdClaim = User.FindFirst("CompanyId")?.Value;
        return long.TryParse(companyIdClaim, out var companyId) ? companyId : null;
    }

    /// <summary>
    /// Checks if current user can access a user from the specified company
    /// SysAdmin can access any company, others only their own
    /// </summary>
    private bool CanAccessUser(long? targetCompanyId)
    {
        var currentUserRole = GetCurrentUserRole();

        // SysAdmin can access any user
        if (currentUserRole == EUserRole.SysAdmin)
        {
            return true;
        }

        var currentUserCompanyId = GetCurrentUserCompanyId();

        // Users can only access users from their own company
        return targetCompanyId == currentUserCompanyId;
    }

    #endregion
}

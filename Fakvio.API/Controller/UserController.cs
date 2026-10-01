using Fakvio.Contracts.Common.Pagination;
using Fakvio.Contracts.Dto.User;
using Fakvio.Application.Service;
using Fakvio.Domain.Enums;
using Fakvio.Infrastructure.Service;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.Extensions.Configuration;

namespace Fakvio.API.Controller;

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
    private readonly ISystemConfigurationService _systemConfigService;
    private readonly ICaptchaService _captchaService;
    private readonly IConfiguration _configuration;
    private readonly ILogger<UserController> _logger;

    public UserController(
        IUserService userService,
        IEmailService emailService,
        ISystemConfigurationService systemConfigService,
        ICaptchaService captchaService,
        IConfiguration configuration,
        ILogger<UserController> logger)
    {
        _userService = userService;
        _emailService = emailService;
        _systemConfigService = systemConfigService;
        _captchaService = captchaService;
        _configuration = configuration;
        _logger = logger;
    }

    /// <summary>
    /// Gets all users
    /// Only SysAdmin can list users. Other roles use their own profile endpoint.
    /// </summary>
    /// <param name="companyId">Filter by company ID (SysAdmin only)</param>
    /// <param name="includeInactive">Include inactive users</param>
    /// <returns>List of users</returns>
    /// <response code="200">Returns list of users</response>
    /// <response code="403">Only SysAdmin may list users</response>
    [HttpGet]
    [Authorize(Roles = "SysAdmin")]
    [ProducesResponseType(typeof(List<UserDto>), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    public async Task<ActionResult<List<UserDto>>> GetAllUsers(
        [FromQuery] long? companyId = null,
        [FromQuery] bool includeInactive = false)
    {
        try
        {
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
    /// <response code="403">Only SysAdmin may list users</response>
    [HttpGet("paged")]
    [Authorize(Roles = "SysAdmin")]
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
            // Accountants and ordinary users may read only their own profile.
            if (!CanAccessUser(id)) return Forbid();

            var user = await _userService.GetUserByIdAsync(id);

            if (user == null)
            {
                return NotFound(new { message = $"User with ID {id} not found." });
            }

            // Recheck account access before returning personal data.
            if (!CanAccessUser(id))
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
    /// Only SysAdmin can create users.
    /// </summary>
    /// <param name="createDto">User creation data</param>
    /// <returns>Created user</returns>
    /// <response code="201">User created successfully</response>
    /// <response code="400">Invalid data or email already exists</response>
    /// <response code="403">Not authorized to create users</response>
    [HttpPost]
    [Authorize(Roles = "SysAdmin")] // Only SysAdmin can create users
    [ProducesResponseType(typeof(UserDto), StatusCodes.Status201Created)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    public async Task<ActionResult<UserDto>> CreateUser([FromBody] CreateUserDto createDto)
    {
        try
        {
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
    /// Only SysAdmin can update user accounts, roles, and company membership.
    /// </summary>
    /// <param name="id">User ID to update</param>
    /// <param name="updateDto">Updated user data</param>
    /// <returns>Updated user</returns>
    /// <response code="200">User updated successfully</response>
    /// <response code="400">Invalid data</response>
    /// <response code="403">Not authorized to update this user</response>
    /// <response code="404">User not found</response>
    [HttpPut("{id}")]
    [Authorize(Roles = "SysAdmin")]
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
            if (!CanAccessUser(id))
            {
                return Forbid();
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
    /// Users can change their own password; only SysAdmin can change another user's password.
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
            // Only SysAdmin may change another account's password.
            if (!CanAccessUser(id)) return Forbid();

            // Get target user
            var targetUser = await _userService.GetUserByIdAsync(id);
            if (targetUser == null)
            {
                return NotFound(new { message = $"User with ID {id} not found." });
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
    /// Only SysAdmin can perform this operation.
    /// </summary>
    /// <param name="id">User ID</param>
    /// <param name="dto">New password data</param>
    /// <returns>Success status</returns>
    /// <response code="200">Password reset successfully</response>
    /// <response code="403">Not authorized to reset this user's password</response>
    /// <response code="404">User not found</response>
    [HttpPost("{id}/admin-reset-password")]
    [Authorize(Roles = "SysAdmin")]
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

            // SysAdmin is the only role allowed to reset passwords.
            if (!CanAccessUser(id))
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
    /// Returns the pending invitation token of a user so an admin can rebuild the
    /// set-password link (for example when the invitation email never arrived).
    ///
    /// Why this is a separate, role-gated endpoint instead of a field on UserDto: the token
    /// authenticates the anonymous POST /api/user/set-password call, so exposing it on the
    /// listing DTO handed to every authenticated colleague is an account takeover (issue #364).
    /// SysAdmin can already set any password via
    /// <see cref="AdminResetPassword"/>, so this endpoint grants them nothing new.
    /// </summary>
    /// <param name="id">User ID</param>
    /// <returns>The pending invitation token</returns>
    /// <response code="200">Token returned</response>
    /// <response code="403">Not authorized to read this user's invitation</response>
    /// <response code="404">User not found, or no valid pending invitation</response>
    [HttpGet("{id}/invitation-token")]
    [Authorize(Roles = "SysAdmin")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult> GetInvitationToken(long id)
    {
        try
        {
            var targetUser = await _userService.GetUserByIdAsync(id);
            if (targetUser == null)
            {
                return NotFound(new { message = $"User with ID {id} not found." });
            }

            // SysAdmin is the only role allowed to retrieve invitation credentials.
            if (!CanAccessUser(id))
            {
                return Forbid();
            }

            var token = await _userService.GetPendingInvitationTokenAsync(id);
            if (token == null)
            {
                return NotFound(new { message = $"User with ID {id} has no pending invitation." });
            }

            return Ok(new { token });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error retrieving invitation token for user {UserId}", id);
            return StatusCode(StatusCodes.Status500InternalServerError,
                new { message = "An error occurred while retrieving the invitation token." });
        }
    }

    /// <summary>
    /// Deletes a user (soft delete - sets IsActive = false)
    /// Only SysAdmin can delete user accounts.
    /// </summary>
    /// <param name="id">User ID to delete</param>
    /// <returns>Success status</returns>
    /// <response code="200">User deleted successfully</response>
    /// <response code="403">Not authorized to delete this user</response>
    /// <response code="404">User not found</response>
    [HttpDelete("{id}")]
    [Authorize(Roles = "SysAdmin")]
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
            if (!CanAccessUser(id))
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
    /// Only SysAdmin can invite users.
    /// </summary>
    /// <param name="inviteDto">Invitation data (email, name, role, company)</param>
    /// <returns>Created user data</returns>
    /// <response code="201">User invited successfully</response>
    /// <response code="400">Invalid data or email already exists</response>
    /// <response code="403">Not authorized to invite users</response>
    [HttpPost("invite")]
    [Authorize(Roles = "SysAdmin")]
    [ProducesResponseType(typeof(UserDto), StatusCodes.Status201Created)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    public async Task<ActionResult<UserDto>> InviteUser([FromBody] InviteUserDto inviteDto)
    {
        try
        {
            // Create the user with invitation token. The token comes back on the result type,
            // never on the UserDto that goes into the response body (issue #364).
            var invited = await _userService.InviteUserAsync(inviteDto);
            var user = invited.User;

            // Build the invitation link pointing to the Blazor UI set-password page.
            // Priority: SystemConfiguration DB (SysAdmin-editable) → appsettings.json fallback.
            var blazorBaseUrl = await ResolveBlazorBaseUrlAsync();
            var invitationLink = $"{blazorBaseUrl}/set-password?token={Uri.EscapeDataString(invited.InvitationToken)}";

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
    ///
    /// Setting the password also provisions the tenant workspace. The response therefore
    /// carries WorkspaceReady: false means the password IS set but the workspace could not
    /// be created, so the client must warn the user instead of reporting plain success.
    /// </summary>
    /// <param name="dto">Token and new password</param>
    /// <returns>Result with the password outcome and the workspace readiness flag</returns>
    /// <response code="200">Password set successfully (check WorkspaceReady)</response>
    /// <response code="400">Invalid or expired token</response>
    [HttpPost("set-password")]
    [AllowAnonymous]
    [EnableRateLimiting("auth-anon")]
    [ProducesResponseType(typeof(SetPasswordResultDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public async Task<ActionResult<SetPasswordResultDto>> SetPassword([FromBody] SetPasswordDto dto)
    {
        try
        {
            var result = await _userService.SetPasswordAsync(dto);

            if (!result.PasswordSet)
            {
                return BadRequest(new { message = "Invalid or expired invitation token." });
            }

            if (!result.WorkspaceReady)
            {
                // Not an error for the password itself — but a state the caller must surface.
                // No exception detail is returned; the cause is only in the server logs.
                _logger.LogWarning(
                    "Password set, but the tenant workspace is not provisioned — client is being told so");
            }

            return Ok(result);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error setting password via invitation token");
            return StatusCode(StatusCodes.Status500InternalServerError,
                new { message = "An error occurred while setting the password." });
        }
    }

    /// <summary>
    /// Initiates the "Forgot Password" flow. Generates a reset token and sends an email
    /// with a link to the set-password page. Reuses the invitation token infrastructure
    /// so the existing SetPassword and ValidateInvitationToken endpoints work unchanged.
    ///
    /// SECURITY: Always returns 200 OK regardless of whether the email exists, with the
    /// same fixed body. Prevents enumeration via the RESPONSE — a known, narrower gap
    /// remains via response TIMING (an existing email waits for token persistence + the
    /// awaited SMTP send below; an unknown one returns almost immediately). Pre-existing,
    /// not introduced by RC.3, and out of that task's scope — closing it needs a queued/
    /// fire-and-forget send with a uniform artificial delay on the "unknown" path, which
    /// is a bigger change than adding the CAPTCHA gate below.
    ///
    /// reCAPTCHA v3 gate (RC.3): this endpoint had no bot protection at all — unlike
    /// login/register/ares (AuthController), it could be hammered with arbitrary email
    /// addresses to spam inboxes (email bombing) at no cost to the caller. Same pattern
    /// as AuthController: X-Captcha-Token header, action "forgot_password", 400 on
    /// failure. A 400 here does NOT weaken the anti-enumeration guarantee above — it only
    /// ever says the CAPTCHA check failed, never whether dto.Email exists, so it is
    /// checked BEFORE anything email-specific happens.
    /// </summary>
    /// <param name="dto">Email address of the user requesting password reset.</param>
    /// <returns>Always returns success message (even if email doesn't exist).</returns>
    /// <response code="200">Request accepted (even if email doesn't exist).</response>
    /// <response code="400">CAPTCHA verification failed.</response>
    [HttpPost("forgot-password")]
    [AllowAnonymous]
    [EnableRateLimiting("auth-anon")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public async Task<ActionResult> ForgotPassword([FromBody] ForgotPasswordDto dto)
    {
        var captchaToken = Request.Headers["X-Captcha-Token"].FirstOrDefault();
        if (!await _captchaService.VerifyAsync(captchaToken, "forgot_password"))
        {
            _logger.LogWarning("reCAPTCHA verification failed for forgot-password request");
            return BadRequest(new { message = "CAPTCHA verification failed. Please try again." });
        }

        try
        {
            var token = await _userService.ForgotPasswordAsync(dto.Email);

            // Only send email if user exists (token is non-null).
            // Response is always the same — no info leakage about email existence.
            if (token != null)
            {
                var blazorBaseUrl = await ResolveBlazorBaseUrlAsync();
                var resetLink = $"{blazorBaseUrl}/set-password?token={Uri.EscapeDataString(token)}";

                try
                {
                    // Reuse PasswordResetEmail template type (EContentTemplateType = 22).
                    // Falls back to InvitationEmail template if PasswordResetEmail is not configured.
                    await _emailService.SendInvitationEmailAsync(
                        dto.Email,
                        dto.Email, // Use email as display name (we don't expose user details)
                        resetLink);
                }
                catch (Exception ex)
                {
                    // Email failure should not fail the endpoint — token is already saved.
                    // User can request again if they don't receive the email.
                    _logger.LogError(ex, "Failed to send password reset email to {Email}", dto.Email);
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error processing forgot password for {Email}", dto.Email);
        }

        // Always return success — never reveal whether the email exists
        return Ok(new { message = "If the email exists, a password reset link has been sent." });
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
    [EnableRateLimiting("auth-anon")]
    [ProducesResponseType(typeof(object), StatusCodes.Status200OK)]
    public async Task<ActionResult> ValidateInvitationToken([FromQuery] string token)
    {
        var isValid = await _userService.ValidateInvitationTokenAsync(token);
        return Ok(new { isValid });
    }

    #region Helper Methods

    /// <summary>
    /// Resolves the Blazor UI base URL using a 2-tier fallback:
    ///   1. SystemConfiguration DB (SysAdmin-editable via /system-settings)
    ///   2. appsettings.json "AppSettings:BlazorBaseUrl" (for fresh installs)
    /// Used for building email links (invitation, password reset, etc.).
    /// </summary>
    private async Task<string> ResolveBlazorBaseUrlAsync()
    {
        var dbConfig = await _systemConfigService.GetAsync();
        if (!string.IsNullOrWhiteSpace(dbConfig.BlazorBaseUrl))
            return dbConfig.BlazorBaseUrl.TrimEnd('/');

        return _configuration["AppSettings:BlazorBaseUrl"]?.TrimEnd('/') ?? "https://localhost:5002";
    }

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
    /// Self-service is limited to the authenticated account. Only SysAdmin manages others;
    /// sharing a company does not grant access to a colleague's profile or password.
    /// </summary>
    private bool CanAccessUser(long targetUserId)
        => GetCurrentUserRole() == EUserRole.SysAdmin ||
           (GetCurrentUserId() > 0 && GetCurrentUserId() == targetUserId);
    #endregion
}

using Fakvio.Contracts.Dto.Auth;
using Fakvio.Contracts.Dto.TwoFactor;
using Fakvio.Application.Service;
using Fakvio.Infrastructure.Data;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using System.Security.Claims;

namespace Fakvio.API.Controller;

/// <summary>
/// Controller for Two-Factor Authentication (2FA) operations.
///
/// Endpoints:
/// - GET  /api/twofactor/status              — Get current 2FA status [Authorize]
/// - POST /api/twofactor/totp/setup          — Initiate TOTP setup (QR + key) [Authorize]
/// - POST /api/twofactor/totp/verify         — Verify TOTP code during setup [Authorize]
/// - POST /api/twofactor/email/enable        — Enable email-based 2FA [Authorize]
/// - POST /api/twofactor/verify              — Verify 2FA code during login [AllowAnonymous]
/// - POST /api/twofactor/disable             — Disable 2FA (requires password) [Authorize]
/// - POST /api/twofactor/admin/force-disable — Force-disable 2FA for a user [Admin/SysAdmin]
/// </summary>
[ApiController]
[Route("api/[controller]")]
public class TwoFactorController : ControllerBase
{
    private readonly ITwoFactorService _twoFactorService;
    private readonly IAuthService _authService;
    private readonly MasterDbContext _masterContext;
    private readonly ILogger<TwoFactorController> _logger;

    public TwoFactorController(
        ITwoFactorService twoFactorService,
        IAuthService authService,
        MasterDbContext masterContext,
        ILogger<TwoFactorController> logger)
    {
        _twoFactorService = twoFactorService;
        _authService = authService;
        _masterContext = masterContext;
        _logger = logger;
    }

    // ─── Status ──────────────────────────────────────────────────────────────

    /// <summary>
    /// Gets the current 2FA status for the authenticated user.
    /// Returns whether 2FA is enabled, the method, and when it was enabled.
    /// </summary>
    [HttpGet("status")]
    [Authorize]
    [ProducesResponseType(typeof(TwoFactorStatusDto), StatusCodes.Status200OK)]
    public async Task<ActionResult<TwoFactorStatusDto>> GetStatus()
    {
        var userId = GetCurrentUserId();
        var status = await _twoFactorService.GetTwoFactorStatusAsync(userId);
        return Ok(status);
    }

    // ─── TOTP Setup ──────────────────────────────────────────────────────────

    /// <summary>
    /// Initiates TOTP (authenticator app) setup.
    /// Returns a QR code image, manual entry key, and otpauth:// URI.
    /// The user must scan the QR code and verify a code to complete setup.
    /// </summary>
    [HttpPost("totp/setup")]
    [Authorize]
    [ProducesResponseType(typeof(TotpSetupResponse), StatusCodes.Status200OK)]
    public async Task<ActionResult<TotpSetupResponse>> InitiateTotpSetup()
    {
        try
        {
            var userId = GetCurrentUserId();
            var response = await _twoFactorService.InitiateTotpSetupAsync(userId);
            return Ok(response);
        }
        catch (InvalidOperationException ex)
        {
            return BadRequest(new { message = ex.Message });
        }
    }

    /// <summary>
    /// Verifies the TOTP code during setup.
    /// If the code from the authenticator app matches, 2FA is enabled with TOTP method.
    /// </summary>
    [HttpPost("totp/verify")]
    [Authorize]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public async Task<IActionResult> VerifyTotpSetup([FromBody] VerifyTotpSetupRequest request)
    {
        try
        {
            var userId = GetCurrentUserId();
            var success = await _twoFactorService.VerifyTotpSetupAsync(userId, request.Code);

            if (!success)
            {
                return BadRequest(new { message = "Invalid verification code. Please try again." });
            }

            return Ok(new { message = "TOTP two-factor authentication enabled successfully." });
        }
        catch (InvalidOperationException ex)
        {
            return BadRequest(new { message = ex.Message });
        }
    }

    // ─── Email 2FA ───────────────────────────────────────────────────────────

    /// <summary>
    /// Enables email-based 2FA for the authenticated user.
    /// Requires a verified email address. No setup verification needed.
    /// </summary>
    [HttpPost("email/enable")]
    [Authorize]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public async Task<IActionResult> EnableEmailTwoFactor()
    {
        try
        {
            var userId = GetCurrentUserId();
            await _twoFactorService.EnableEmailTwoFactorAsync(userId);
            return Ok(new { message = "Email two-factor authentication enabled successfully." });
        }
        catch (InvalidOperationException ex)
        {
            return BadRequest(new { message = ex.Message });
        }
    }

    // ─── Login Verification (Step 2) ─────────────────────────────────────────

    /// <summary>
    /// Verifies the 2FA code during login (step 2 of the two-step login flow).
    /// This endpoint is anonymous because the user hasn't received a JWT yet.
    /// On success: generates JWT and returns a full LoginResponse.
    /// </summary>
    [HttpPost("verify")]
    [AllowAnonymous]
    [ProducesResponseType(typeof(LoginResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    public async Task<ActionResult<LoginResponse>> VerifyTwoFactorCode(
        [FromBody] VerifyTwoFactorRequest request)
    {
        try
        {
            // Verify the 2FA code and get the user ID if valid
            var userId = await _twoFactorService.VerifyTwoFactorCodeAsync(
                request.SessionToken, request.Code);

            if (userId == null)
            {
                return Unauthorized(new { message = "Invalid or expired verification code." });
            }

            // 2FA verified — now generate the JWT token (completing the login)
            var user = await _masterContext.User
                .Include(u => u.Company)
                .FirstOrDefaultAsync(u => u.Id == userId.Value);

            if (user == null)
            {
                return Unauthorized(new { message = "User not found." });
            }

            // Update last login time (wasn't updated in step 1)
            user.LastLoginAt = DateTime.UtcNow;
            await _masterContext.SaveChangesAsync();

            // Generate JWT token
            var token = await _authService.GenerateJwtTokenAsync(user.Id);

            // Get token expiration from configuration
            var configuration = HttpContext.RequestServices.GetRequiredService<IConfiguration>();
            var expirationHours = int.TryParse(
                configuration["JwtSettings:ExpirationHours"], out var hours) ? hours : 24;

            _logger.LogInformation(
                "2FA login completed for user {Email}", user.Email);

            return Ok(new LoginResponse
            {
                Token = token,
                ExpiresAt = DateTime.UtcNow.AddHours(expirationHours),
                UserId = user.Id,
                Email = user.Email,
                FullName = user.FullName,
                Role = user.Role,
                CompanyId = user.CompanyId,
                CompanyName = user.Company?.CompanyName,
                IsExternalLogin = user.IsExternalLogin
            });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error during 2FA verification");
            return StatusCode(StatusCodes.Status500InternalServerError,
                new { message = "An error occurred during verification." });
        }
    }

    // ─── Disable 2FA ─────────────────────────────────────────────────────────

    /// <summary>
    /// Disables 2FA for the authenticated user. Requires password confirmation.
    /// </summary>
    [HttpPost("disable")]
    [Authorize]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public async Task<IActionResult> DisableTwoFactor([FromBody] DisableTwoFactorRequest request)
    {
        try
        {
            var userId = GetCurrentUserId();

            // Verify the user's password before allowing 2FA disable
            var user = await _masterContext.User
                .AsNoTracking()
                .FirstOrDefaultAsync(u => u.Id == userId);

            if (user == null || string.IsNullOrEmpty(user.PasswordHash))
            {
                return BadRequest(new { message = "Cannot verify password." });
            }

            if (!_authService.VerifyPassword(request.Password, user.PasswordHash))
            {
                return BadRequest(new { message = "Incorrect password." });
            }

            await _twoFactorService.DisableTwoFactorAsync(userId);
            return Ok(new { message = "Two-factor authentication has been disabled." });
        }
        catch (InvalidOperationException ex)
        {
            return BadRequest(new { message = ex.Message });
        }
    }

    // ─── Admin Force-Disable ─────────────────────────────────────────────────

    /// <summary>
    /// Force-disables 2FA for a user (Admin/SysAdmin only).
    /// Used when a user is locked out and cannot provide a 2FA code.
    /// </summary>
    [HttpPost("admin/force-disable/{userId:long}")]
    [Authorize(Roles = "SysAdmin,Admin")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> ForceDisableTwoFactor(long userId)
    {
        try
        {
            var adminUserId = GetCurrentUserId();
            await _twoFactorService.ForceDisableTwoFactorAsync(userId, adminUserId);
            return Ok(new { message = $"Two-factor authentication disabled for user {userId}." });
        }
        catch (InvalidOperationException ex)
        {
            return NotFound(new { message = ex.Message });
        }
    }

    // ─── Private Helpers ─────────────────────────────────────────────────────

    /// <summary>
    /// Extracts the current user's ID from the JWT claims.
    /// Throws if the claim is missing (shouldn't happen on [Authorize] endpoints).
    /// </summary>
    private long GetCurrentUserId()
    {
        var userIdClaim = User.FindFirstValue(ClaimTypes.NameIdentifier);
        if (string.IsNullOrEmpty(userIdClaim) || !long.TryParse(userIdClaim, out var userId))
        {
            throw new InvalidOperationException("User ID claim not found in token.");
        }
        return userId;
    }
}

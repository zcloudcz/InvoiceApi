using Fakvio.Contracts.Dto.Auth;
using Fakvio.Application.Service;
using Fakvio.Domain.Enums;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using System.Security.Claims;

namespace Fakvio.API.Controller;

/// <summary>
/// Controller for authentication operations.
/// Handles login, self-registration, email verification, and external OAuth login.
/// </summary>
[ApiController]
[Route("api/[controller]")]
public class AuthController : ControllerBase
{
    private readonly IAuthService _authService;
    private readonly ILogger<AuthController> _logger;

    public AuthController(IAuthService authService, ILogger<AuthController> logger)
    {
        _authService = authService;
        _logger = logger;
    }

    // ─── Login ───────────────────────────────────────────────────────────────

    /// <summary>
    /// Authenticates user and returns JWT token
    /// </summary>
    /// <param name="loginRequest">User credentials (email and password)</param>
    /// <returns>Login response with JWT token or 401 Unauthorized</returns>
    /// <response code="200">Login successful, returns JWT token and user info</response>
    /// <response code="401">Invalid credentials or user is inactive</response>
    [HttpPost("login")]
    [AllowAnonymous]
    [ProducesResponseType(typeof(LoginResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    public async Task<ActionResult<LoginResponse>> Login([FromBody] LoginRequest loginRequest)
    {
        try
        {
            var response = await _authService.LoginAsync(loginRequest);

            if (response == null)
            {
                _logger.LogWarning("Failed login attempt for email: {Email}", loginRequest.Email);
                return Unauthorized(new { message = "Invalid email or password." });
            }

            _logger.LogInformation("Successful login for user: {Email}", loginRequest.Email);
            return Ok(response);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error during login for email: {Email}", loginRequest.Email);
            return StatusCode(StatusCodes.Status500InternalServerError, new { message = "An error occurred during login." });
        }
    }

    // ─── Self-Registration ───────────────────────────────────────────────────

    /// <summary>
    /// Registers a new company and admin user.
    /// Creates the company, system settings, and user records.
    /// Sends a verification email with a link to confirm the email address.
    /// </summary>
    /// <param name="request">Registration details (email, password, company info, optional IČO)</param>
    /// <returns>Registration response with user ID and verification instructions</returns>
    /// <response code="200">Registration successful — verification email sent</response>
    /// <response code="400">Validation error (e.g., email already taken)</response>
    [HttpPost("register")]
    [AllowAnonymous]
    [ProducesResponseType(typeof(RegisterResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public async Task<ActionResult<RegisterResponse>> Register([FromBody] RegisterRequest request)
    {
        try
        {
            // Build the base URL from the current request for the verification link
            var baseUrl = $"{Request.Scheme}://{Request.Host}";

            var response = await _authService.RegisterAsync(request, baseUrl);

            _logger.LogInformation("New registration for email: {Email}", request.Email);
            return Ok(response);
        }
        catch (InvalidOperationException ex)
        {
            // Business logic errors (duplicate email, etc.)
            _logger.LogWarning(ex, "Registration failed for email: {Email}", request.Email);
            return BadRequest(new { message = ex.Message });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Unexpected error during registration for email: {Email}", request.Email);
            return StatusCode(StatusCodes.Status500InternalServerError,
                new { message = "An error occurred during registration." });
        }
    }

    // ─── Email Verification ──────────────────────────────────────────────────

    /// <summary>
    /// Verifies the user's email address using the token from the verification link.
    /// On success, the user's email is marked as verified and their tenant database is provisioned.
    /// </summary>
    /// <param name="request">Verification token from the email link</param>
    /// <returns>Success or failure status</returns>
    /// <response code="200">Email verified successfully</response>
    /// <response code="400">Invalid or expired token</response>
    [HttpPost("verify-email")]
    [AllowAnonymous]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public async Task<ActionResult> VerifyEmail([FromBody] VerifyEmailRequest request)
    {
        try
        {
            var result = await _authService.VerifyEmailAsync(request.Token);

            if (!result)
            {
                return BadRequest(new { message = "Invalid or expired verification token." });
            }

            return Ok(new { message = "Email verified successfully. You can now log in." });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error during email verification");
            return StatusCode(StatusCodes.Status500InternalServerError,
                new { message = "An error occurred during email verification." });
        }
    }

    // ─── External OAuth Login ────────────────────────────────────────────────

    /// <summary>
    /// Initiates external OAuth login by redirecting the user to the provider's login page.
    /// After authentication, the provider redirects back to /api/auth/external-callback.
    /// </summary>
    /// <param name="provider">OAuth provider name: Google, Microsoft, Facebook, Apple, Seznam</param>
    /// <param name="returnUrl">URL to redirect to after successful authentication (default: /)</param>
    /// <response code="302">Redirects to the OAuth provider's login page</response>
    /// <response code="400">Invalid provider name</response>
    [HttpGet("external-login")]
    [AllowAnonymous]
    public ActionResult ExternalLogin([FromQuery] string provider, [FromQuery] string? returnUrl = "/")
    {
        // Validate provider name
        if (!Enum.TryParse<EExternalProvider>(provider, ignoreCase: true, out var parsed) ||
            parsed == EExternalProvider.None)
        {
            return BadRequest(new { message = $"Invalid OAuth provider: {provider}" });
        }

        // Build the callback URL that the OAuth provider will redirect to
        var callbackUrl = Url.Action(nameof(ExternalCallback), "Auth",
            new { returnUrl }, Request.Scheme);

        // Challenge the specified authentication scheme — redirects the browser to the provider
        var properties = new AuthenticationProperties
        {
            RedirectUri = callbackUrl,
            Items = { { "provider", provider } }
        };

        return Challenge(properties, provider);
    }

    /// <summary>
    /// Handles the callback from an external OAuth provider after the user authenticates.
    /// Extracts user info from the external claims, then either logs in (existing user)
    /// or redirects to registration (new user).
    /// </summary>
    /// <param name="returnUrl">URL to redirect to after processing</param>
    /// <response code="302">Redirects to returnUrl with JWT token or to registration page</response>
    [HttpGet("external-callback")]
    [AllowAnonymous]
    public async Task<ActionResult> ExternalCallback([FromQuery] string? returnUrl = "/")
    {
        try
        {
            // Read the external authentication result
            var authenticateResult = await HttpContext.AuthenticateAsync();

            if (!authenticateResult.Succeeded || authenticateResult.Principal == null)
            {
                _logger.LogWarning("External authentication failed");
                return Redirect($"/login?error=ExternalAuthFailed");
            }

            var claims = authenticateResult.Principal.Claims.ToList();

            // Extract user info from external provider claims
            var email = claims.FirstOrDefault(c => c.Type == ClaimTypes.Email)?.Value
                ?? claims.FirstOrDefault(c => c.Type == "email")?.Value;
            var firstName = claims.FirstOrDefault(c => c.Type == ClaimTypes.GivenName)?.Value
                ?? claims.FirstOrDefault(c => c.Type == "given_name")?.Value
                ?? "";
            var lastName = claims.FirstOrDefault(c => c.Type == ClaimTypes.Surname)?.Value
                ?? claims.FirstOrDefault(c => c.Type == "family_name")?.Value
                ?? "";
            var providerId = claims.FirstOrDefault(c => c.Type == ClaimTypes.NameIdentifier)?.Value
                ?? claims.FirstOrDefault(c => c.Type == "sub")?.Value
                ?? "";

            // Determine which provider was used
            var providerName = authenticateResult.Properties?.Items
                .FirstOrDefault(x => x.Key == "provider").Value ?? "Unknown";

            if (string.IsNullOrEmpty(email))
            {
                _logger.LogWarning("External auth succeeded but no email claim found from {Provider}", providerName);
                return Redirect("/login?error=NoEmail");
            }

            if (!Enum.TryParse<EExternalProvider>(providerName, ignoreCase: true, out var externalProvider))
            {
                externalProvider = EExternalProvider.None;
            }

            // Try to login or link the external account
            var callbackDto = new ExternalLoginCallbackDto
            {
                Email = email,
                FirstName = firstName,
                LastName = lastName,
                Provider = externalProvider,
                ProviderId = providerId
            };

            var loginResponse = await _authService.ExternalLoginAsync(callbackDto);

            if (loginResponse != null)
            {
                // Existing user — redirect with JWT token
                _logger.LogInformation("External login successful for {Email} via {Provider}",
                    email, providerName);
                return Redirect($"/auth-callback?token={loginResponse.Token}");
            }

            // New user — redirect to registration with pre-filled data
            var encodedEmail = Uri.EscapeDataString(email);
            var encodedFirstName = Uri.EscapeDataString(firstName);
            var encodedLastName = Uri.EscapeDataString(lastName);
            return Redirect(
                $"/register?provider={providerName}&providerId={Uri.EscapeDataString(providerId)}" +
                $"&email={encodedEmail}&firstName={encodedFirstName}&lastName={encodedLastName}");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error processing external auth callback");
            return Redirect("/login?error=CallbackError");
        }
    }

    // ─── Token Validation / Refresh ──────────────────────────────────────────

    /// <summary>
    /// Validates the current JWT token.
    /// Useful for checking if user is still authenticated.
    /// </summary>
    /// <returns>Current user info if token is valid</returns>
    /// <response code="200">Token is valid, returns user info</response>
    /// <response code="401">Token is invalid or expired</response>
    [HttpGet("validate")]
    [Authorize]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    public ActionResult ValidateToken()
    {
        // If we reach here, the token is valid (enforced by [Authorize])
        var userId = User.FindFirst(ClaimTypes.NameIdentifier)?.Value;
        var email = User.FindFirst(ClaimTypes.Email)?.Value;
        var role = User.FindFirst(ClaimTypes.Role)?.Value;
        var companyId = User.FindFirst("CompanyId")?.Value;

        return Ok(new
        {
            userId,
            email,
            role,
            companyId,
            message = "Token is valid"
        });
    }

    /// <summary>
    /// Refreshes the JWT token for authenticated user.
    /// Generates a new token with updated expiration.
    /// </summary>
    /// <returns>New JWT token</returns>
    /// <response code="200">Token refreshed successfully</response>
    /// <response code="401">User not authenticated</response>
    [HttpPost("refresh")]
    [Authorize]
    [ProducesResponseType(typeof(LoginResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    public async Task<ActionResult<LoginResponse>> RefreshToken()
    {
        try
        {
            var userIdClaim = User.FindFirst(ClaimTypes.NameIdentifier)?.Value;

            if (string.IsNullOrEmpty(userIdClaim) || !long.TryParse(userIdClaim, out var userId))
            {
                return Unauthorized(new { message = "Invalid user ID in token." });
            }

            // Generate new token
            var token = await _authService.GenerateJwtTokenAsync(userId);

            var expirationHours = 24; // Could be read from configuration
            var expiresAt = DateTime.UtcNow.AddHours(expirationHours);

            var email = User.FindFirst(ClaimTypes.Email)?.Value ?? "";
            var fullName = User.FindFirst(ClaimTypes.Name)?.Value ?? "";
            var role = User.FindFirst(ClaimTypes.Role)?.Value ?? "";
            var companyIdClaim = User.FindFirst("CompanyId")?.Value;

            long? companyId = null;
            if (!string.IsNullOrEmpty(companyIdClaim) && long.TryParse(companyIdClaim, out var parsedCompanyId))
            {
                companyId = parsedCompanyId;
            }

            return Ok(new LoginResponse
            {
                Token = token,
                ExpiresAt = expiresAt,
                UserId = userId,
                Email = email,
                FullName = fullName,
                Role = Enum.Parse<EUserRole>(role),
                CompanyId = companyId,
                CompanyName = null // Could fetch from database if needed
            });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error during token refresh");
            return StatusCode(StatusCodes.Status500InternalServerError, new { message = "An error occurred during token refresh." });
        }
    }
}

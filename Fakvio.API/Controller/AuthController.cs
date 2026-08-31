using AresService;
using Fakvio.Contracts.Dto.Auth;
using Fakvio.Application.Service;
using Fakvio.Domain.Enums;
using Fakvio.Infrastructure.Service;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using System.Security.Claims;

namespace Fakvio.API.Controller;

/// <summary>
/// Controller for authentication operations.
/// Handles login, self-registration, email verification, and external OAuth login.
/// Also exposes the anonymous ARES lookup the registration form needs (see FetchFromAres).
/// reCAPTCHA v3 is validated on login and register endpoints via X-Captcha-Token header.
/// </summary>
[ApiController]
[Route("api/[controller]")]
public class AuthController : ControllerBase
{
    private readonly IAuthService _authService;
    private readonly ISystemConfigurationService _systemConfigService;
    private readonly ICaptchaService _captchaService;
    // ARES registry lookup — used by the anonymous endpoint the registration form calls.
    private readonly IAresService _aresService;
    private readonly IConfiguration _configuration;
    private readonly ILogger<AuthController> _logger;

    public AuthController(
        IAuthService authService,
        ISystemConfigurationService systemConfigService,
        ICaptchaService captchaService,
        IAresService aresService,
        IConfiguration configuration,
        ILogger<AuthController> logger)
    {
        _authService = authService;
        _systemConfigService = systemConfigService;
        _captchaService = captchaService;
        _aresService = aresService;
        _configuration = configuration;
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
            // Validate reCAPTCHA v3 token (sent via X-Captcha-Token header from Blazor UI).
            // The action must match the one Login.razor passes to grecaptcha.execute(),
            // otherwise a token minted on another page would be accepted here.
            var captchaToken = Request.Headers["X-Captcha-Token"].FirstOrDefault();
            if (!await _captchaService.VerifyAsync(captchaToken, "login"))
            {
                _logger.LogWarning("reCAPTCHA verification failed for login: {Email}", loginRequest.Email);
                return BadRequest(new { message = "CAPTCHA verification failed. Please try again." });
            }

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
            // Validate reCAPTCHA v3 token — action must match Register.razor's grecaptcha.execute("register").
            var captchaToken = Request.Headers["X-Captcha-Token"].FirstOrDefault();
            if (!await _captchaService.VerifyAsync(captchaToken, "register"))
            {
                _logger.LogWarning("reCAPTCHA verification failed for registration: {Email}", request.Email);
                return BadRequest(new { message = "CAPTCHA verification failed. Please try again." });
            }

            // Use the Blazor UI base URL for the set-password link in the email.
            // The /set-password page lives in the Blazor WASM app, not the API.
            // Priority: SystemConfiguration DB → appsettings.json → current request host
            var dbConfig = await _systemConfigService.GetAsync();
            var blazorBaseUrl = !string.IsNullOrWhiteSpace(dbConfig.BlazorBaseUrl)
                ? dbConfig.BlazorBaseUrl.TrimEnd('/')
                : _configuration["AppSettings:BlazorBaseUrl"]?.TrimEnd('/')
                    ?? $"{Request.Scheme}://{Request.Host}";

            var response = await _authService.RegisterAsync(request, blazorBaseUrl);

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

    /// <summary>
    /// Looks up a company in the ARES registry for the self-registration form.
    /// Anonymous by design — /register is a page for a user who has no account yet,
    /// so the tenant-scoped ClientController.FetchFromAres (which requires a JWT)
    /// cannot be used from there.
    ///
    /// Abuse protection, in order of importance:
    /// 1. The same reCAPTCHA v3 gate as login/register (X-Captcha-Token header).
    ///    This is the mechanism the repo already uses for anonymous endpoints and it
    ///    works in both hosts (API and Azure Functions), unlike ASP.NET rate-limiting
    ///    middleware, which the Functions host would silently skip.
    /// 2. Cache-first lookup (GetCompanyInfoAsync, not RefreshCompanyInfoAsync): a
    ///    caller cannot force unbounded outbound traffic to the public ARES registry
    ///    by replaying the same IČO.
    /// 3. Input is validated here (8 digits) — a malformed IČO never leaves our host.
    /// 4. The response is a narrow AresLookupResponse (name + registered office), not
    ///    the full ClientDto. Registration needs nothing else.
    /// </summary>
    /// <param name="registrationNumber">Czech registration number (IČO), exactly 8 digits</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>Company name and registered office from ARES</returns>
    /// <response code="200">Company found</response>
    /// <response code="400">Malformed IČO, failed CAPTCHA, or company not found in ARES</response>
    [HttpGet("ares/{registrationNumber}")]
    [AllowAnonymous]
    [ProducesResponseType(typeof(AresLookupResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public async Task<ActionResult<AresLookupResponse>> FetchFromAres(
        string registrationNumber,
        CancellationToken cancellationToken = default)
    {
        // Validate reCAPTCHA v3 token — action must match Register.razor's grecaptcha.execute("ares").
        // This endpoint proxies an external registry, so a token issued for another action
        // (e.g. the registration form itself) must not open it.
        var captchaToken = Request.Headers["X-Captcha-Token"].FirstOrDefault();
        if (!await _captchaService.VerifyAsync(captchaToken, "ares"))
        {
            _logger.LogWarning("reCAPTCHA verification failed for anonymous ARES lookup");
            return BadRequest(new { message = "CAPTCHA verification failed. Please try again." });
        }

        // Fail fast at the boundary: a malformed IČO is a client bug, not a registry outage.
        if (!IsValidRegistrationNumber(registrationNumber))
        {
            return BadRequest(new { message = "Registration number must be exactly 8 digits." });
        }

        try
        {
            var info = await _aresService.GetCompanyInfoAsync(registrationNumber, cancellationToken);

            if (!info.IsSuccessful)
            {
                // Not found / registry error — the caller only needs to know the lookup failed.
                // ErrorMessage is deliberately NOT echoed back: AresServiceImpl builds some of
                // those strings from raw exception text (connection errors, parse errors), and
                // this endpoint is reachable without a login. The detail goes to the log only.
                _logger.LogInformation(
                    "Anonymous ARES lookup for {RegistrationNumber} failed: {Error}",
                    registrationNumber, info.ErrorMessage);
                return BadRequest(new { message = "Company not found in ARES." });
            }

            return Ok(new AresLookupResponse
            {
                RegistrationNumber = info.RegistrationNumber,
                CompanyName = info.CompanyName,
                // Address is null when ARES has no "sidlo" block — the form then stays empty.
                Street = info.Address?.Street ?? string.Empty,
                City = info.Address?.City ?? string.Empty,
                PostalCode = info.Address?.PostalCode ?? string.Empty,
                Country = info.Address?.Country ?? string.Empty
            });
        }
        catch (Exception ex)
        {
            // Never leak registry/internal details to an anonymous caller.
            _logger.LogError(ex, "Unexpected error during anonymous ARES lookup for {RegistrationNumber}",
                registrationNumber);
            return StatusCode(StatusCodes.Status500InternalServerError,
                new { message = "An error occurred during the ARES lookup." });
        }
    }

    /// <summary>
    /// A Czech IČO is exactly 8 digits. Same rule as AresServiceImpl — checked here too
    /// so that garbage input is rejected before any outbound call is made.
    ///
    /// ASCII '0'-'9' only, NOT char.IsDigit (issue #200): char.IsDigit accepts every
    /// Unicode decimal digit, so eight Arabic-Indic digits used to pass this guard,
    /// reach the registry, and add another key to the shared ARES cache.
    /// </summary>
    private static bool IsValidRegistrationNumber(string? registrationNumber)
        => !string.IsNullOrWhiteSpace(registrationNumber)
           && registrationNumber.Length == 8
           && registrationNumber.All(c => c is >= '0' and <= '9');

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

            if (!result.EmailVerified)
            {
                return BadRequest(new { message = result.Message });
            }

            // Email verified — return full response including provisioning status.
            // HTTP 200 even if provisioning failed (email IS verified, tenant can be retried).
            return Ok(result);
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

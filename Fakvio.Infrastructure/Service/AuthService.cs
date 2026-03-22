using AresService;
using Fakvio.Contracts.Dto.Auth;
using Fakvio.Application.Service;
using Fakvio.Domain.Entities;
using Fakvio.Domain.Enums;
using Fakvio.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.IdentityModel.Tokens;
using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Text;
using BCrypt.Net;

namespace Fakvio.Infrastructure.Service;

/// <summary>
/// Service for authentication operations.
/// Handles login, self-registration, email verification, external OAuth login,
/// JWT token generation, and password hashing.
/// </summary>
public class AuthService : IAuthService
{
    private readonly MasterDbContext _context;
    private readonly IConfiguration _configuration;
    private readonly ITenantProvisioningService _provisioningService;
    private readonly IEmailService _emailService;
    private readonly IAresService _aresService;
    private readonly ILogger<AuthService> _logger;
    private readonly ITwoFactorService _twoFactorService;

    public AuthService(
        MasterDbContext context,
        IConfiguration configuration,
        ITenantProvisioningService provisioningService,
        IEmailService emailService,
        IAresService aresService,
        ILogger<AuthService> logger,
        ITwoFactorService twoFactorService)
    {
        _context = context;
        _configuration = configuration;
        _provisioningService = provisioningService;
        _emailService = emailService;
        _aresService = aresService;
        _logger = logger;
        _twoFactorService = twoFactorService;
    }

    // ─── Login ───────────────────────────────────────────────────────────────

    /// <summary>
    /// Authenticates user with email and password.
    /// Returns JWT token and user info if successful, null otherwise.
    /// Rejects: OAuth-only users, unverified emails, inactive accounts, pending invitations.
    /// </summary>
    public async Task<LoginResponse?> LoginAsync(LoginRequest loginRequest, CancellationToken cancellationToken = default)
    {
        // Find user by email (include Company for the response)
        var user = await _context.User
            .Include(u => u.Company)
            .FirstOrDefaultAsync(u => u.Email == loginRequest.Email, cancellationToken);

        // Return null if user not found or inactive
        if (user == null || !user.IsActive)
        {
            return null;
        }

        // OAuth-only users cannot use password login — they have no local password
        if (user.IsExternalLogin)
        {
            _logger.LogWarning(
                "Password login attempted for OAuth user {Email} (provider: {Provider})",
                user.Email, user.ExternalProvider);
            return null;
        }

        // Unverified email — user must verify before logging in
        if (!user.IsEmailVerified)
        {
            _logger.LogWarning("Login attempted for unverified email: {Email}", user.Email);
            return null;
        }

        // Pending invitation — user must set their password first
        if (user.IsInvitationPending)
        {
            return null;
        }

        // Verify password — null PasswordHash means no local password (shouldn't happen for non-OAuth users)
        if (string.IsNullOrEmpty(user.PasswordHash) || !VerifyPassword(loginRequest.Password, user.PasswordHash))
        {
            return null;
        }

        // ── Two-Factor Authentication check ──────────────────────────────────
        // If 2FA is enabled, don't issue a JWT yet — return a session token
        // for the second step (code verification).
        if (user.TwoFactorEnabled)
        {
            var sessionToken = await _twoFactorService.GenerateTwoFactorSessionTokenAsync(
                user.Id, cancellationToken);

            _logger.LogInformation(
                "2FA required for user {Email} (method: {Method})",
                user.Email, user.TwoFactorMethod);

            return new LoginResponse
            {
                RequiresTwoFactor = true,
                TwoFactorSessionToken = sessionToken,
                TwoFactorMethod = user.TwoFactorMethod,
                // Don't set Token — the user hasn't completed authentication yet
                UserId = user.Id,
                Email = user.Email,
                FullName = user.FullName,
                Role = user.Role,
                CompanyId = user.CompanyId,
                CompanyName = user.Company?.CompanyName,
                IsExternalLogin = user.IsExternalLogin
            };
        }

        // Update last login time
        user.LastLoginAt = DateTime.UtcNow;
        await _context.SaveChangesAsync(cancellationToken);

        // Generate JWT token
        var token = await GenerateJwtTokenAsync(user.Id, cancellationToken);

        // Get token expiration from configuration (default 24 hours)
        var expirationHours = int.TryParse(_configuration["JwtSettings:ExpirationHours"], out var hours) ? hours : 24;
        var expiresAt = DateTime.UtcNow.AddHours(expirationHours);

        // Build and return login response
        return new LoginResponse
        {
            Token = token,
            ExpiresAt = expiresAt,
            UserId = user.Id,
            Email = user.Email,
            FullName = user.FullName,
            Role = user.Role,
            CompanyId = user.CompanyId,
            CompanyName = user.Company?.CompanyName,
            IsExternalLogin = user.IsExternalLogin
        };
    }

    // ─── Self-Registration ───────────────────────────────────────────────────

    /// <summary>
    /// Self-registers a new company and admin user.
    /// Flow:
    /// 1. Validate email uniqueness
    /// 2. Optionally call ARES to fetch company data by IČO
    /// 3. Create Client (issuer) with company info
    /// 4. Create CompanySystemSettings (not yet provisioned)
    /// 5. Create User (Admin role, unverified email, verification token)
    /// 6. Send verification email
    /// </summary>
    public async Task<RegisterResponse> RegisterAsync(RegisterRequest request, string baseUrl, CancellationToken ct = default)
    {
        // 1. Validate email uniqueness — same check as in UserService.CreateUserAsync
        var existingUser = await _context.User
            .AnyAsync(u => u.Email == request.Email, ct);

        if (existingUser)
        {
            throw new InvalidOperationException($"User with email '{request.Email}' already exists.");
        }

        // 2. IČO (registration number) is required — call ARES to fetch company details
        string companyName = request.CompanyName;
        string registrationNumber = request.RegistrationNumber;
        string? taxNumber = null;

        try
        {
            var aresInfo = await _aresService.GetCompanyInfoAsync(request.RegistrationNumber, ct);
            // Use ARES data if available, otherwise fall back to user-provided values
            companyName = !string.IsNullOrWhiteSpace(aresInfo.CompanyName)
                ? aresInfo.CompanyName
                : request.CompanyName;
            registrationNumber = aresInfo.RegistrationNumber ?? request.RegistrationNumber;
            taxNumber = aresInfo.TaxNumber;
        }
        catch (Exception ex)
        {
            // ARES lookup failure is not fatal — continue with user-provided data
            _logger.LogWarning(ex, "ARES lookup failed for IČO {Ico}, continuing with manual data", request.RegistrationNumber);
        }

        // 3. Create Client (issuer) — the company record in the master database
        var client = new Client
        {
            CompanyName = companyName,
            RegistrationNumber = registrationNumber,
            TaxNumber = taxNumber,
            IsIssuer = true,
            IsActive = true
        };

        _context.Client.Add(client);
        await _context.SaveChangesAsync(ct);

        // 4. Create CompanySystemSettings — tenant schema will be provisioned after email verification.
        // Schema name follows PostgreSQL naming convention: "tenant_{companyId}".
        var schemaName = $"tenant_{client.Id}";
        var companySettings = new CompanySystemSettings
        {
            CompanyId = client.Id,
            SchemaName = schemaName,
            IsProvisioned = false,
            IsActive = true
        };

        _context.CompanySystemSettings.Add(companySettings);
        await _context.SaveChangesAsync(ct);

        // 5. Create User — Admin role for the self-registered company owner.
        // Password is NOT set during registration — the user sets it after email verification.
        // We use InvitationToken (not EmailVerificationToken) so that the set-password flow
        // handles both password creation and email verification in one step.
        var invitationToken = Guid.NewGuid().ToString();
        var tokenExpiresAt = DateTime.UtcNow.AddHours(24);

        var user = new User
        {
            Email = request.Email,
            PasswordHash = null, // No password at registration — set via /set-password link
            FirstName = request.FirstName,
            LastName = request.LastName,
            Role = EUserRole.Admin,
            CompanyId = client.Id,
            IsActive = true,
            IsEmailVerified = false,
            IsInvitationPending = true,
            InvitationToken = invitationToken,
            InvitationTokenExpiresAt = tokenExpiresAt,
            ExternalProvider = EExternalProvider.None
        };

        _context.User.Add(user);
        await _context.SaveChangesAsync(ct);

        // 6. Send "set your password" email — this link also verifies the email address.
        // Track success/failure so the frontend can inform the user.
        var emailSent = false;
        string? emailError = null;

        try
        {
            var setPasswordLink = $"{baseUrl.TrimEnd('/')}/set-password?token={invitationToken}";
            var subject = "Set your password — Fakvio";
            var htmlBody = $@"
                <div style=""font-family: Arial, sans-serif; max-width: 600px; margin: 0 auto;"">
                    <h2 style=""color: #1976D2;"">Welcome to Fakvio</h2>
                    <p>Hello <strong>{user.FullName}</strong>,</p>
                    <p>Thank you for registering. Please set your password by clicking the button below:</p>
                    <div style=""text-align: center; margin: 30px 0;"">
                        <a href=""{setPasswordLink}""
                           style=""background-color: #1976D2; color: white; padding: 14px 28px;
                                  text-decoration: none; border-radius: 4px; font-size: 16px;"">
                            Set Password
                        </a>
                    </div>
                    <p style=""color: #666; font-size: 14px;"">This link is valid for 24 hours.</p>
                    <p style=""color: #666; font-size: 14px;"">If you didn't create this account, you can safely ignore this email.</p>
                    <hr style=""border: none; border-top: 1px solid #eee; margin: 20px 0;"" />
                    <p style=""color: #999; font-size: 12px;"">Fakvio</p>
                </div>";

            await _emailService.SendEmailAsync(request.Email, subject, htmlBody, ct: ct);
            emailSent = true;
            _logger.LogInformation(
                "Registration email SENT to {Email} for company '{CompanyName}' (ClientId={ClientId}, UserId={UserId})",
                request.Email, companyName, client.Id, user.Id);
        }
        catch (Exception ex)
        {
            // Email failure should not block registration — user can request resend later.
            emailError = ex.Message;
            _logger.LogError(ex,
                "Registration email FAILED for {Email}, company '{CompanyName}' (ClientId={ClientId}, UserId={UserId}): {Error}",
                request.Email, companyName, client.Id, user.Id, ex.Message);
        }

        _logger.LogInformation(
            "New company registered: {CompanyName} (ClientId={ClientId}), User: {Email} (UserId={UserId}), EmailSent={EmailSent}",
            companyName, client.Id, request.Email, user.Id, emailSent);

        return new RegisterResponse
        {
            UserId = user.Id,
            Email = user.Email,
            Message = emailSent
                ? "Registration successful. Please check your email to set your password."
                : "Registration successful, but the email could not be sent. Please contact support.",
            RequiresEmailVerification = true,
            EmailSent = emailSent,
            EmailError = emailError
        };
    }

    // ─── Email Verification ──────────────────────────────────────────────────

    /// <summary>
    /// Verifies a user's email using the token from the verification link.
    /// On success: sets IsEmailVerified = true, clears token, triggers tenant provisioning.
    /// </summary>
    public async Task<VerifyEmailResponse> VerifyEmailAsync(string token, CancellationToken ct = default)
    {
        // Find user by verification token
        var user = await _context.User
            .FirstOrDefaultAsync(u => u.EmailVerificationToken == token, ct);

        if (user == null)
        {
            _logger.LogWarning("Email verification attempted with invalid token");
            return new VerifyEmailResponse
            {
                EmailVerified = false,
                TenantProvisioned = false,
                Message = "Invalid or expired verification token."
            };
        }

        // Check if token has expired (24 hours from registration)
        if (user.EmailVerificationTokenExpiresAt.HasValue &&
            user.EmailVerificationTokenExpiresAt.Value < DateTime.UtcNow)
        {
            _logger.LogWarning("Email verification attempted with expired token for user {Email}", user.Email);
            return new VerifyEmailResponse
            {
                EmailVerified = false,
                TenantProvisioned = false,
                Message = "Verification token has expired. Please request a new one."
            };
        }

        // Mark email as verified and clear token fields
        user.IsEmailVerified = true;
        user.EmailVerificationToken = null;
        user.EmailVerificationTokenExpiresAt = null;
        user.UpdatedAt = DateTime.UtcNow;

        await _context.SaveChangesAsync(ct);

        // Trigger tenant database provisioning for the user's company.
        // Provisioning failure should NOT fail the email verification — it can be retried by SysAdmin.
        // But we now surface the error in the response so the UI and logs show what happened.
        var tenantProvisioned = false;
        string? provisioningError = null;

        if (user.CompanyId.HasValue)
        {
            try
            {
                await _provisioningService.ProvisionTenantAsync(user.CompanyId.Value, ct);
                tenantProvisioned = true;
                _logger.LogInformation(
                    "Tenant provisioned for company {CompanyId} after email verification by user {Email}",
                    user.CompanyId.Value, user.Email);
            }
            catch (Exception ex)
            {
                // Log the full error chain — ProvisionTenantAsync now includes step info in the message.
                provisioningError = ex.Message;
                _logger.LogError(ex,
                    "Tenant provisioning failed for company {CompanyId} after email verification by user {Email}",
                    user.CompanyId.Value, user.Email);
            }
        }

        _logger.LogInformation("Email verified for user {Email} (UserId={UserId}), TenantProvisioned={TenantProvisioned}",
            user.Email, user.Id, tenantProvisioned);

        return new VerifyEmailResponse
        {
            EmailVerified = true,
            TenantProvisioned = tenantProvisioned,
            Message = tenantProvisioned
                ? "Email verified successfully. Your workspace is ready."
                : "Email verified successfully, but workspace setup failed. Please contact support.",
            ProvisioningError = provisioningError
        };
    }

    // ─── External OAuth Login ────────────────────────────────────────────────

    /// <summary>
    /// Handles login for users authenticated via external OAuth providers.
    /// Three scenarios:
    /// 1. User exists by Provider+ProviderId → login (return JWT)
    /// 2. User exists by email with no external provider → link the OAuth account and login
    /// 3. User not found → return null (caller redirects to registration)
    /// </summary>
    public async Task<LoginResponse?> ExternalLoginAsync(ExternalLoginCallbackDto dto, CancellationToken ct = default)
    {
        // Scenario 1: Find by external provider + provider ID (exact match)
        var user = await _context.User
            .Include(u => u.Company)
            .FirstOrDefaultAsync(
                u => u.ExternalProvider == dto.Provider && u.ExternalProviderId == dto.ProviderId,
                ct);

        if (user != null)
        {
            // Known OAuth user — update last login and return JWT
            user.LastLoginAt = DateTime.UtcNow;
            await _context.SaveChangesAsync(ct);

            return await BuildLoginResponseAsync(user, ct);
        }

        // Scenario 2: Find by email — link the OAuth account if no provider is set
        user = await _context.User
            .Include(u => u.Company)
            .FirstOrDefaultAsync(u => u.Email == dto.Email, ct);

        if (user != null)
        {
            if (user.ExternalProvider == EExternalProvider.None)
            {
                // Local user exists with this email — link the OAuth provider
                user.ExternalProvider = dto.Provider;
                user.ExternalProviderId = dto.ProviderId;
                user.IsEmailVerified = true; // OAuth provider has verified the email
                user.LastLoginAt = DateTime.UtcNow;
                await _context.SaveChangesAsync(ct);

                _logger.LogInformation(
                    "Linked {Provider} OAuth to existing user {Email}",
                    dto.Provider, dto.Email);

                return await BuildLoginResponseAsync(user, ct);
            }

            // User already has a different OAuth provider — conflict
            _logger.LogWarning(
                "OAuth conflict: {Email} already linked to {ExistingProvider}, attempted {NewProvider}",
                dto.Email, user.ExternalProvider, dto.Provider);
            return null;
        }

        // Scenario 3: No user found — caller should redirect to registration
        _logger.LogInformation(
            "OAuth user not found: {Email} via {Provider}, needs registration",
            dto.Email, dto.Provider);
        return null;
    }

    // ─── JWT Token Generation ────────────────────────────────────────────────

    /// <summary>
    /// Generates JWT token for authenticated user.
    /// Includes claims: UserId, Email, Role, CompanyId (if applicable).
    /// </summary>
    public async Task<string> GenerateJwtTokenAsync(long userId, CancellationToken cancellationToken = default)
    {
        // Fetch user with company data
        var user = await _context.User
            .Include(u => u.Company)
            .FirstOrDefaultAsync(u => u.Id == userId, cancellationToken);

        if (user == null)
        {
            throw new InvalidOperationException($"User with ID {userId} not found.");
        }

        // Build claims list
        var claims = new List<Claim>
        {
            new Claim(ClaimTypes.NameIdentifier, user.Id.ToString()),
            new Claim(ClaimTypes.Email, user.Email),
            new Claim(ClaimTypes.Name, user.FullName),
            new Claim(ClaimTypes.Role, user.Role.ToString())
        };

        // Add CompanyId claim if user belongs to a company
        if (user.CompanyId.HasValue)
        {
            claims.Add(new Claim("CompanyId", user.CompanyId.Value.ToString()));
        }

        // Get JWT settings from configuration
        var secret = _configuration["JwtSettings:Secret"]
            ?? throw new InvalidOperationException("JWT Secret not configured in appsettings.json");
        var issuer = _configuration["JwtSettings:Issuer"] ?? "Fakvio";
        var audience = _configuration["JwtSettings:Audience"] ?? "FakvioClient";
        var expirationHours = int.TryParse(_configuration["JwtSettings:ExpirationHours"], out var hours) ? hours : 24;

        // Create signing key
        var key = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(secret));
        var credentials = new SigningCredentials(key, SecurityAlgorithms.HmacSha256);

        // Create JWT token
        var token = new JwtSecurityToken(
            issuer: issuer,
            audience: audience,
            claims: claims,
            expires: DateTime.UtcNow.AddHours(expirationHours),
            signingCredentials: credentials
        );

        // Return serialized token
        return new JwtSecurityTokenHandler().WriteToken(token);
    }

    // ─── Password Utilities ──────────────────────────────────────────────────

    /// <summary>
    /// Hashes password using BCrypt with work factor 12.
    /// Work factor 12 provides good balance between security and performance.
    /// </summary>
    public string HashPassword(string password)
    {
        return BCrypt.Net.BCrypt.HashPassword(password, workFactor: 12);
    }

    /// <summary>
    /// Verifies plain text password against BCrypt hash.
    /// Uses constant-time comparison to prevent timing attacks.
    /// </summary>
    public bool VerifyPassword(string password, string passwordHash)
    {
        try
        {
            return BCrypt.Net.BCrypt.Verify(password, passwordHash);
        }
        catch
        {
            // Return false if hash is invalid or verification fails
            return false;
        }
    }

    // ─── Private Helpers ─────────────────────────────────────────────────────

    /// <summary>
    /// Builds a LoginResponse with JWT token for an authenticated user.
    /// Reused by LoginAsync and ExternalLoginAsync to avoid duplication.
    /// </summary>
    private async Task<LoginResponse> BuildLoginResponseAsync(User user, CancellationToken ct)
    {
        var token = await GenerateJwtTokenAsync(user.Id, ct);
        var expirationHours = int.TryParse(_configuration["JwtSettings:ExpirationHours"], out var hours) ? hours : 24;

        return new LoginResponse
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
        };
    }
}

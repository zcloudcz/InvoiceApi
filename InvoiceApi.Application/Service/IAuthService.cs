using InvoiceApi.Contracts.Dto.Auth;

namespace InvoiceApi.Application.Service;

/// <summary>
/// Service for authentication operations.
/// Handles login, self-registration, email verification, external OAuth login,
/// JWT token generation, and password hashing.
/// </summary>
public interface IAuthService
{
    /// <summary>
    /// Authenticates user with email and password.
    /// Returns null if credentials are invalid, user is inactive, email is unverified,
    /// or user is an OAuth-only account (no password).
    /// </summary>
    /// <param name="loginRequest">Login credentials</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>Login response with JWT token or null if authentication fails</returns>
    Task<LoginResponse?> LoginAsync(LoginRequest loginRequest, CancellationToken cancellationToken = default);

    /// <summary>
    /// Generates JWT token for authenticated user
    /// </summary>
    /// <param name="userId">User ID</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>JWT token string</returns>
    Task<string> GenerateJwtTokenAsync(long userId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Hashes password using BCrypt
    /// </summary>
    /// <param name="password">Plain text password</param>
    /// <returns>Hashed password</returns>
    string HashPassword(string password);

    /// <summary>
    /// Verifies password against hash
    /// </summary>
    /// <param name="password">Plain text password</param>
    /// <param name="passwordHash">Hashed password from database</param>
    /// <returns>True if password matches</returns>
    bool VerifyPassword(string password, string passwordHash);

    /// <summary>
    /// Self-registers a new company and admin user.
    /// Creates a Client (issuer), CompanySystemSettings, and User record.
    /// Sends a verification email with a token link.
    /// Optionally calls ARES to auto-fill company data if IČO is provided.
    /// </summary>
    /// <param name="request">Registration details (email, password, company info)</param>
    /// <param name="baseUrl">Base URL for building the verification link (e.g. "https://app.example.com")</param>
    /// <param name="ct">Cancellation token</param>
    /// <returns>Registration response with user ID and verification status</returns>
    Task<RegisterResponse> RegisterAsync(RegisterRequest request, string baseUrl, CancellationToken ct = default);

    /// <summary>
    /// Verifies a user's email address using the token from the verification link.
    /// Sets IsEmailVerified = true and triggers tenant provisioning.
    /// </summary>
    /// <param name="token">Email verification token (GUID string)</param>
    /// <param name="ct">Cancellation token</param>
    /// <returns>True if verification succeeded; false if token is invalid or expired</returns>
    Task<bool> VerifyEmailAsync(string token, CancellationToken ct = default);

    /// <summary>
    /// Handles login/linking for users authenticated via an external OAuth provider.
    /// If the user exists (by provider+ID), logs them in.
    /// If found by email with no provider, links the OAuth account.
    /// Returns null if the user needs to register first.
    /// </summary>
    /// <param name="dto">OAuth callback data (email, name, provider, providerId)</param>
    /// <param name="ct">Cancellation token</param>
    /// <returns>Login response with JWT token, or null if user must register first</returns>
    Task<LoginResponse?> ExternalLoginAsync(ExternalLoginCallbackDto dto, CancellationToken ct = default);
}

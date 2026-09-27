using Fakvio.UI.Shared.Models;
using Microsoft.AspNetCore.Components.Authorization;

namespace Fakvio.UI.Shared.Services;

/// <summary>
/// Blazor API client for Two-Factor Authentication operations.
/// Inherits ApiClientBase for shared auth headers, logging, and error handling.
///
/// Used by:
/// - TwoFactorSettings page (enable/disable 2FA)
/// - TwoFactorVerification page (verify code during login)
/// - Login page (check if 2FA is required)
/// </summary>
public class TwoFactorApiService : ApiClientBase
{
    public TwoFactorApiService(
        IHttpClientFactory httpClientFactory,
        ILogger<TwoFactorApiService> logger,
        AuthenticationStateProvider authStateProvider)
        : base(httpClientFactory, logger, authStateProvider)
    {
    }

    /// <summary>
    /// Gets the current 2FA status for the authenticated user.
    /// </summary>
    public async Task<TwoFactorStatusDto?> GetStatusAsync()
    {
        return await GetAsync<TwoFactorStatusDto>("/api/twofactor/status");
    }

    /// <summary>
    /// Initiates TOTP (authenticator app) setup.
    /// Returns QR code image, manual key, and otpauth:// URI.
    /// </summary>
    public async Task<TotpSetupResponse?> InitiateTotpSetupAsync()
    {
        return await PostWithoutBodyAsync<TotpSetupResponse>("/api/twofactor/totp/setup");
    }

    /// <summary>
    /// Verifies the TOTP code during setup — completes TOTP 2FA activation.
    /// </summary>
    public async Task<bool> VerifyTotpSetupAsync(string code)
    {
        return await PostBoolAsync("/api/twofactor/totp/verify", new { Code = code });
    }

    /// <summary>
    /// Enables email-based 2FA for the current user.
    /// </summary>
    public async Task<bool> EnableEmailTwoFactorAsync()
    {
        return await PostWithoutBodyBoolAsync("/api/twofactor/email/enable");
    }

    /// <summary>
    /// Verifies the 2FA code during login (step 2 of two-step login).
    /// This is called from the TwoFactorVerification page and does NOT require
    /// an auth header (the user doesn't have a JWT yet).
    /// Returns a full LoginResponse with JWT token on success.
    /// </summary>
    public async Task<LoginResponse?> VerifyTwoFactorCodeAsync(string sessionToken, string code)
    {
        try
        {
            var response = await _httpClient.PostAsJsonAsync("/api/twofactor/verify",
                new { SessionToken = sessionToken, Code = code });

            if (response.IsSuccessStatusCode)
            {
                return await response.Content.ReadFromJsonAsync<LoginResponse>();
            }

            _logger.LogWarning("2FA verify failed with status {StatusCode}", response.StatusCode);

            // RC.4 — a 429 from the rate limiter is not "invalid code"; the caller shows a
            // distinct message instead of the generic 2FA failure.
            if (RateLimitExceededException.Matches(response.StatusCode))
                throw new RateLimitExceededException();

            return null;
        }
        catch (RateLimitExceededException)
        {
            throw; // Already logged above.
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error during 2FA verification");
            return null;
        }
    }

    /// <summary>
    /// Disables 2FA for the current user. Requires password confirmation.
    /// </summary>
    public async Task<bool> DisableTwoFactorAsync(string password)
    {
        return await PostBoolAsync("/api/twofactor/disable", new { Password = password });
    }
}

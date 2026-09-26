using Fakvio.UI.Shared.Models;
using System.Net.Http.Json;
// Import only VerifyEmailResponse from Contracts — other Auth DTOs (LoginRequest, etc.)
// are defined in Fakvio.UI.Shared.Models and would cause ambiguous reference errors.
using AresLookupResponse = Fakvio.Contracts.Dto.Auth.AresLookupResponse;
using VerifyEmailResponse = Fakvio.Contracts.Dto.Auth.VerifyEmailResponse;

namespace Fakvio.UI.Shared.Services;

/// <summary>
/// Service for authentication API calls.
/// Handles login, registration, email verification, and token validation.
///
/// Derives from ApiClientBase so errors are forwarded to the server-side AppLog table
/// (via IClientLogger wired by AddApiClient&lt;T&gt;). Auth endpoints need custom
/// HttpRequestMessages (X-Captcha-Token header, manual Bearer token), so this class
/// builds its own requests instead of using the base GET/POST helpers — but it reuses
/// the base class's ForwardToServerLog/LogClientException so failures still reach AppLog.
/// Without this, login/registration errors would only ever show in the browser console.
///
/// SECURITY: never log request bodies here — they contain passwords. Only HTTP status,
/// endpoint, and server-returned error text are forwarded.
/// </summary>
public class AuthApiService : ApiClientBase
{
    public AuthApiService(IHttpClientFactory httpClientFactory, ILogger<AuthApiService> logger)
        : base(httpClientFactory, logger)
    {
    }

    /// <summary>
    /// Authenticates user with email and password.
    /// Sends reCAPTCHA v3 token via X-Captcha-Token header for bot protection.
    /// </summary>
    public async Task<LoginResponse?> LoginAsync(LoginRequest loginRequest, string? captchaToken = null)
    {
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, "/api/auth/login");
            request.Content = JsonContent.Create(loginRequest);
            if (!string.IsNullOrEmpty(captchaToken))
                request.Headers.Add("X-Captcha-Token", captchaToken);

            var response = await _httpClient.SendAsync(request);

            if (!response.IsSuccessStatusCode)
            {
                // Failed login is a Warning (wrong password is a user error, not a system error),
                // but it still belongs in AppLog — it is a useful security/audit signal.
                _logger.LogWarning("POST /api/auth/login failed with status {StatusCode}", response.StatusCode);
                ForwardToServerLog(
                    "Warning",
                    $"POST /api/auth/login failed: HTTP {(int)response.StatusCode} {response.StatusCode}",
                    null,
                    "AuthApiService");

                // A failed CAPTCHA is not "wrong password" — Login.razor shows a distinct
                // message for it (see CaptchaException), so it must not be swallowed into
                // the plain "return null" every other 400/401 gets here.
                var errorContent = await response.Content.ReadAsStringAsync();
                if (CaptchaException.Matches(response.StatusCode, errorContent))
                    throw new CaptchaException();

                return null;
            }

            return await response.Content.ReadFromJsonAsync<LoginResponse>();
        }
        catch (CaptchaException)
        {
            throw; // Already logged above — let Login.razor show its dedicated message.
        }
        catch (Exception ex)
        {
            // Transport-level failure (server down, network, deserialization).
            LogClientException(ex, "POST", "/api/auth/login");
            return null;
        }
    }

    /// <summary>
    /// Registers a new company and admin user.
    /// Sends reCAPTCHA v3 token via X-Captcha-Token header for bot protection.
    /// </summary>
    public async Task<RegisterResponse?> RegisterAsync(RegisterRequest registerRequest, string? captchaToken = null)
    {
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, "/api/auth/register");
            request.Content = JsonContent.Create(registerRequest);
            if (!string.IsNullOrEmpty(captchaToken))
                request.Headers.Add("X-Captcha-Token", captchaToken);

            var response = await _httpClient.SendAsync(request);

            if (!response.IsSuccessStatusCode)
            {
                var errorContent = await response.Content.ReadAsStringAsync();
                _logger.LogWarning("POST /api/auth/register failed with status {StatusCode}: {Error}",
                    response.StatusCode, errorContent);
                ForwardToServerLog(
                    "Warning",
                    $"POST /api/auth/register failed: HTTP {(int)response.StatusCode} {response.StatusCode}",
                    errorContent,
                    "AuthApiService");

                // Same distinction as LoginAsync: a failed CAPTCHA gets its own exception
                // type so Register.razor can show a dedicated message instead of echoing
                // the raw server text (which, for every OTHER 400, is what we want here).
                if (CaptchaException.Matches(response.StatusCode, errorContent))
                    throw new CaptchaException();

                throw new InvalidOperationException(
                    !string.IsNullOrEmpty(errorContent) ? errorContent : "Registration failed.");
            }

            return await response.Content.ReadFromJsonAsync<RegisterResponse>();
        }
        catch (CaptchaException)
        {
            throw; // Already logged above — don't log twice.
        }
        catch (InvalidOperationException)
        {
            throw; // Already logged above — don't log twice.
        }
        catch (Exception ex)
        {
            LogClientException(ex, "POST", "/api/auth/register");
            throw new InvalidOperationException($"Registration failed: {ex.Message}", ex);
        }
    }

    /// <summary>
    /// Looks up a company in ARES by its registration number (IČO) for the registration form.
    ///
    /// Uses the anonymous endpoint on AuthController — NOT /api/client/ares/{ico}, which is
    /// tenant-scoped and requires a JWT the visitor of /register does not have yet.
    /// Sends the reCAPTCHA v3 token via X-Captcha-Token header, same as login/register,
    /// because that is what protects the anonymous endpoint from being used as a free
    /// ARES proxy.
    ///
    /// Returns null when the company was not found or the lookup failed — the caller
    /// shows a localized message; there is nothing actionable to bubble up.
    /// </summary>
    public async Task<AresLookupResponse?> FetchFromAresAsync(string registrationNumber, string? captchaToken = null)
    {
        var endpoint = $"/api/auth/ares/{Uri.EscapeDataString(registrationNumber)}";

        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, endpoint);
            if (!string.IsNullOrEmpty(captchaToken))
                request.Headers.Add("X-Captcha-Token", captchaToken);

            var response = await _httpClient.SendAsync(request);

            if (!response.IsSuccessStatusCode)
            {
                // Unknown IČO is a normal outcome here, so this is a Warning, not an Error.
                _logger.LogWarning("GET {Endpoint} failed with status {StatusCode}",
                    endpoint, response.StatusCode);
                ForwardToServerLog(
                    "Warning",
                    $"GET {endpoint} failed: HTTP {(int)response.StatusCode} {response.StatusCode}",
                    null,
                    "AuthApiService");

                // A failed CAPTCHA is not "IČO not found" — Register.razor shows a distinct
                // message for it, so it must not fall through to the generic null/"failed" path.
                var errorContent = await response.Content.ReadAsStringAsync();
                if (CaptchaException.Matches(response.StatusCode, errorContent))
                    throw new CaptchaException();

                return null;
            }

            return await response.Content.ReadFromJsonAsync<AresLookupResponse>();
        }
        catch (CaptchaException)
        {
            throw; // Already logged above — let Register.razor show its dedicated message.
        }
        catch (Exception ex)
        {
            // Transport-level failure (server down, network, deserialization).
            LogClientException(ex, "GET", endpoint);
            return null;
        }
    }

    /// <summary>
    /// Verifies a user's email using the token from the verification link.
    /// Returns a detailed response with email verification AND tenant provisioning status.
    /// This allows the UI to show a warning if provisioning failed (workspace not ready).
    /// </summary>
    public async Task<VerifyEmailResponse> VerifyEmailAsync(string token)
    {
        try
        {
            var response = await _httpClient.PostAsJsonAsync("/api/auth/verify-email",
                new { Token = token });

            if (response.IsSuccessStatusCode)
            {
                // Deserialize the full response to get provisioning details
                var result = await response.Content.ReadFromJsonAsync<VerifyEmailResponse>();
                return result ?? new VerifyEmailResponse
                {
                    EmailVerified = true,
                    TenantProvisioned = true,
                    Message = "Email verified successfully."
                };
            }

            // API returned 400 (invalid/expired token) — try to read error message
            var errorContent = await response.Content.ReadAsStringAsync();
            _logger.LogWarning("POST /api/auth/verify-email failed with status {StatusCode}: {Error}",
                response.StatusCode, errorContent);
            ForwardToServerLog(
                "Warning",
                $"POST /api/auth/verify-email failed: HTTP {(int)response.StatusCode} {response.StatusCode}",
                errorContent,
                "AuthApiService");
            return new VerifyEmailResponse
            {
                EmailVerified = false,
                TenantProvisioned = false,
                Message = errorContent
            };
        }
        catch (Exception ex)
        {
            LogClientException(ex, "POST", "/api/auth/verify-email");
            return new VerifyEmailResponse
            {
                EmailVerified = false,
                TenantProvisioned = false,
                Message = ex.Message
            };
        }
    }

    /// <summary>
    /// Validates the current JWT token.
    /// A non-success response is routine (expired token on app start) and is NOT logged;
    /// only transport-level exceptions are forwarded to AppLog.
    /// </summary>
    public async Task<bool> ValidateTokenAsync(string token)
    {
        try
        {
            var request = new HttpRequestMessage(HttpMethod.Get, "/api/auth/validate");
            request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", token);

            var response = await _httpClient.SendAsync(request);
            return response.IsSuccessStatusCode;
        }
        catch (Exception ex)
        {
            LogClientException(ex, "GET", "/api/auth/validate");
            return false;
        }
    }
}

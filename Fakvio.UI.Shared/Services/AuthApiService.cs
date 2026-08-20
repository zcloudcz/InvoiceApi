using Fakvio.UI.Shared.Models;
using System.Net.Http.Json;

namespace Fakvio.UI.Shared.Services;

/// <summary>
/// Service for authentication API calls.
/// Handles login, registration, and token validation.
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
                return null;
            }

            return await response.Content.ReadFromJsonAsync<LoginResponse>();
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
                throw new InvalidOperationException(
                    !string.IsNullOrEmpty(errorContent) ? errorContent : "Registration failed.");
            }

            return await response.Content.ReadFromJsonAsync<RegisterResponse>();
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

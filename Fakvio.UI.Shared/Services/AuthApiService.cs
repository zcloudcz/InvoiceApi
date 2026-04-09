using Fakvio.UI.Shared.Models;
using System.Net.Http.Json;
// Import only VerifyEmailResponse from Contracts — other Auth DTOs (LoginRequest, etc.)
// are defined in Fakvio.UI.Shared.Models and would cause ambiguous reference errors.
using VerifyEmailResponse = Fakvio.Contracts.Dto.Auth.VerifyEmailResponse;

namespace Fakvio.UI.Shared.Services;

/// <summary>
/// Service for authentication API calls.
/// Handles login, registration, email verification, and token validation.
/// </summary>
public class AuthApiService
{
    private readonly HttpClient _httpClient;

    public AuthApiService(IHttpClientFactory httpClientFactory)
    {
        _httpClient = httpClientFactory.CreateClient("InvoiceAPI");
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
                return null;

            return await response.Content.ReadFromJsonAsync<LoginResponse>();
        }
        catch (Exception)
        {
            return null;
        }
    }

    /// <summary>
    /// Registers a new company and admin user.
    /// Returns registration response with verification instructions.
    /// </summary>
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
                throw new InvalidOperationException(
                    !string.IsNullOrEmpty(errorContent) ? errorContent : "Registration failed.");
            }

            return await response.Content.ReadFromJsonAsync<RegisterResponse>();
        }
        catch (InvalidOperationException)
        {
            throw;
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException($"Registration failed: {ex.Message}", ex);
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
            return new VerifyEmailResponse
            {
                EmailVerified = false,
                TenantProvisioned = false,
                Message = errorContent
            };
        }
        catch (Exception ex)
        {
            return new VerifyEmailResponse
            {
                EmailVerified = false,
                TenantProvisioned = false,
                Message = ex.Message
            };
        }
    }

    /// <summary>
    /// Validates the current JWT token
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
        catch (Exception)
        {
            return false;
        }
    }
}

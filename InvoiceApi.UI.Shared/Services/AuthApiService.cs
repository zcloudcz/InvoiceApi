using InvoiceApi.UI.Shared.Models;
using System.Net.Http.Json;

namespace InvoiceApi.UI.Shared.Services;

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
    /// Authenticates user with email and password
    /// </summary>
    public async Task<LoginResponse?> LoginAsync(LoginRequest loginRequest)
    {
        try
        {
            var response = await _httpClient.PostAsJsonAsync("/api/auth/login", loginRequest);

            if (!response.IsSuccessStatusCode)
            {
                return null;
            }

            var loginResponse = await response.Content.ReadFromJsonAsync<LoginResponse>();
            return loginResponse;
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
    public async Task<RegisterResponse?> RegisterAsync(RegisterRequest request)
    {
        try
        {
            var response = await _httpClient.PostAsJsonAsync("/api/auth/register", request);

            if (!response.IsSuccessStatusCode)
            {
                // Try to extract error message from response body
                var errorContent = await response.Content.ReadAsStringAsync();
                throw new InvalidOperationException(
                    !string.IsNullOrEmpty(errorContent) ? errorContent : "Registration failed.");
            }

            return await response.Content.ReadFromJsonAsync<RegisterResponse>();
        }
        catch (InvalidOperationException)
        {
            throw; // Re-throw business errors
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException($"Registration failed: {ex.Message}", ex);
        }
    }

    /// <summary>
    /// Verifies a user's email using the token from the verification link.
    /// Returns true if verification succeeded.
    /// </summary>
    public async Task<bool> VerifyEmailAsync(string token)
    {
        try
        {
            var response = await _httpClient.PostAsJsonAsync("/api/auth/verify-email",
                new { Token = token });

            return response.IsSuccessStatusCode;
        }
        catch (Exception)
        {
            return false;
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

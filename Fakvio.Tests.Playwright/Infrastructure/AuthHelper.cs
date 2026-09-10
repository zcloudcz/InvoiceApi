using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Fakvio.Tests.Playwright.Infrastructure;

/// <summary>
/// Static helper that authenticates against the Fakvio API and returns
/// the serialized session JSON for injection into browser localStorage.
///
/// IMPORTANT: The API returns camelCase JSON (ASP.NET Core default),
/// but Blazor's CustomAuthenticationStateProvider stores PascalCase JSON
/// because C#'s JsonSerializer.Serialize uses PascalCase by default.
/// This helper converts the API response to PascalCase to match
/// what the Blazor app expects in localStorage.
/// </summary>
public static class AuthHelper
{
    /// <summary>
    /// Calls POST /api/auth/login, converts the response to PascalCase JSON
    /// (matching Blazored.LocalStorage format), and returns it.
    /// This JSON can be injected directly into localStorage as "UserSession".
    /// </summary>
    public static async Task<string> GetSessionJsonAsync(string apiUrl, string email, string password)
    {
        using var http = new HttpClient { BaseAddress = new Uri(apiUrl) };

        var response = await http.PostAsJsonAsync("api/auth/login", new
        {
            Email = email,
            Password = password
        });

        response.EnsureSuccessStatusCode();

        // Read the API response as camelCase JSON
        var camelJson = await response.Content.ReadAsStringAsync();

        // Deserialize with camelCase, then re-serialize with PascalCase
        // to match what Blazor's JsonSerializer.Serialize(loginResponse) produces
        var options = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };
        var loginResponse = JsonSerializer.Deserialize<LoginResponseDto>(camelJson, options)
            ?? throw new InvalidOperationException("Failed to deserialize login response");

        if (string.IsNullOrEmpty(loginResponse.Token))
        {
            throw new InvalidOperationException(
                $"Login response does not contain a token. Response: {camelJson[..Math.Min(camelJson.Length, 200)]}");
        }

        // Serialize with default PascalCase (matches Blazor app behavior)
        return JsonSerializer.Serialize(loginResponse);
    }

    /// <summary>
    /// Gets just the JWT token string for use in direct API calls.
    /// </summary>
    public static async Task<string> GetTokenAsync(string apiUrl, string email, string password)
    {
        using var http = new HttpClient { BaseAddress = new Uri(apiUrl) };

        var response = await http.PostAsJsonAsync("api/auth/login", new
        {
            Email = email,
            Password = password
        });

        response.EnsureSuccessStatusCode();

        var json = await response.Content.ReadAsStringAsync();
        using var doc = JsonDocument.Parse(json);
        return doc.RootElement.GetProperty("token").GetString()
            ?? throw new InvalidOperationException("Token is null in login response.");
    }

    /// <summary>
    /// Reads the pending "set your password" token of a freshly registered user.
    ///
    /// Self-registration mails the token to the new user, and a browser test cannot open that
    /// mailbox — polling SMTP would also make the run depend on a mail server being up. The
    /// token is therefore read back through the API, in two steps: the paged list resolves the
    /// email to a user id, and the role-gated
    /// <c>GET /api/user/{id}/invitation-token</c> returns the token itself.
    ///
    /// It used to be one step, reading <c>UserDto.InvitationToken</c> straight off
    /// <c>GET /api/user/paged</c>. That property is gone: the listing endpoints are open to
    /// every authenticated member of a company and the token authenticates the anonymous
    /// set-password call, so it was an account-takeover primitive (issue #364). SysAdmin can
    /// still reach the token, because SysAdmin can already reset any password anyway.
    /// </summary>
    /// <param name="apiUrl">REST API base URL.</param>
    /// <param name="sysAdminToken">JWT of a SysAdmin — see <see cref="GetTokenAsync"/>.</param>
    /// <param name="email">Email address the user registered with.</param>
    /// <exception cref="InvalidOperationException">
    /// No user with that email has a pending invitation — e.g. registration silently failed,
    /// or the password was already set.
    /// </exception>
    public static async Task<string> GetInvitationTokenAsync(string apiUrl, string sysAdminToken, string email)
    {
        using var http = new HttpClient { BaseAddress = new Uri(apiUrl) };
        http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", sysAdminToken);

        // Search narrows the page server-side; the email match below is what actually decides,
        // because Search is a "contains" filter and could return neighbouring accounts.
        var listJson = await http.GetStringAsync(
            $"api/user/paged?Search={Uri.EscapeDataString(email)}&PageSize=5");

        long? userId = null;
        using (var listDoc = JsonDocument.Parse(listJson))
        {
            foreach (var user in listDoc.RootElement.GetProperty("items").EnumerateArray())
            {
                if (string.Equals(user.GetProperty("email").GetString(), email, StringComparison.OrdinalIgnoreCase))
                {
                    userId = user.GetProperty("id").GetInt64();
                    break;
                }
            }
        }

        if (userId is null)
        {
            throw new InvalidOperationException(
                $"No user found for '{email}'. Did the registration succeed?");
        }

        // 404 here means the invitation is not pending any more (password already set, or the
        // token expired) — same "nothing usable" outcome the caller has to treat as a failure.
        var tokenResponse = await http.GetAsync($"api/user/{userId}/invitation-token");
        if (!tokenResponse.IsSuccessStatusCode)
        {
            throw new InvalidOperationException(
                $"No pending invitation token for '{email}' (user {userId}): " +
                $"{(int)tokenResponse.StatusCode} {tokenResponse.ReasonPhrase}.");
        }

        using var tokenDoc = JsonDocument.Parse(await tokenResponse.Content.ReadAsStringAsync());
        return tokenDoc.RootElement.GetProperty("token").GetString()
            ?? throw new InvalidOperationException($"Invitation token is null for '{email}'.");
    }

    /// <summary>
    /// Minimal DTO matching the LoginResponse model used by the Blazor app.
    /// Properties use PascalCase which matches C# JsonSerializer defaults.
    /// </summary>
    private class LoginResponseDto
    {
        public string Token { get; set; } = "";
        public DateTime ExpiresAt { get; set; }
        public long UserId { get; set; }
        public string Email { get; set; } = "";
        public string FullName { get; set; } = "";
        public int Role { get; set; }
        public long? CompanyId { get; set; }
        public string? CompanyName { get; set; }
        public bool IsExternalLogin { get; set; }
        public bool RequiresTwoFactor { get; set; }
        public string? TwoFactorSessionToken { get; set; }
        public int? TwoFactorMethod { get; set; }
    }
}

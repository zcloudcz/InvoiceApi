using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.AspNetCore.Components.Server.ProtectedBrowserStorage;
using System.Security.Claims;
using System.Text.Json;

namespace InvoiceApi.BlazorUI.Services;

/// <summary>
/// Custom authentication state provider for Blazor Server
/// Stores authentication state in protected browser storage
/// </summary>
public class CustomAuthenticationStateProvider : AuthenticationStateProvider
{
    private readonly ProtectedSessionStorage _sessionStorage;
    private readonly AuthApiService _authApiService;
    private ClaimsPrincipal _anonymous = new ClaimsPrincipal(new ClaimsIdentity());

    public CustomAuthenticationStateProvider(
        ProtectedSessionStorage sessionStorage,
        AuthApiService authApiService)
    {
        _sessionStorage = sessionStorage;
        _authApiService = authApiService;
    }

    /// <summary>
    /// Gets the current authentication state
    /// </summary>
    public override async Task<AuthenticationState> GetAuthenticationStateAsync()
    {
        try
        {
            // Try to get user session from protected storage
            var userSessionResult = await _sessionStorage.GetAsync<string>("UserSession");

            if (!userSessionResult.Success || string.IsNullOrEmpty(userSessionResult.Value))
            {
                return new AuthenticationState(_anonymous);
            }

            var userSession = JsonSerializer.Deserialize<Models.LoginResponse>(userSessionResult.Value);

            if (userSession == null)
            {
                return new AuthenticationState(_anonymous);
            }

            // Check if token is expired
            if (userSession.ExpiresAt <= DateTime.UtcNow)
            {
                await ClearSessionAsync();
                return new AuthenticationState(_anonymous);
            }

            // Create claims principal from session
            var claimsPrincipal = CreateClaimsPrincipal(userSession);

            // If SysAdmin is impersonating a company, add the impersonated company as a claim.
            // This allows role-based checks in the UI (e.g., showing invoicing menu items).
            if (userSession.Role == 2) // SysAdmin
            {
                try
                {
                    var impersonatedResult = await _sessionStorage.GetAsync<long>("ImpersonatedCompanyId");
                    if (impersonatedResult.Success)
                    {
                        var identity = claimsPrincipal.Identity as ClaimsIdentity;
                        identity?.AddClaim(new Claim("ImpersonatedCompanyId", impersonatedResult.Value.ToString()));

                        var nameResult = await _sessionStorage.GetAsync<string>("ImpersonatedCompanyName");
                        if (nameResult.Success)
                        {
                            identity?.AddClaim(new Claim("ImpersonatedCompanyName", nameResult.Value ?? ""));
                        }
                    }
                }
                catch
                {
                    // Ignore — session might not be available yet
                }
            }

            return new AuthenticationState(claimsPrincipal);
        }
        catch (Exception)
        {
            return new AuthenticationState(_anonymous);
        }
    }

    /// <summary>
    /// Marks the user as authenticated after successful login
    /// </summary>
    public async Task MarkUserAsAuthenticatedAsync(Models.LoginResponse loginResponse)
    {
        // Store user session in protected storage
        var userSessionJson = JsonSerializer.Serialize(loginResponse);
        await _sessionStorage.SetAsync("UserSession", userSessionJson);

        // Create claims principal
        var claimsPrincipal = CreateClaimsPrincipal(loginResponse);

        // Notify authentication state changed
        NotifyAuthenticationStateChanged(Task.FromResult(new AuthenticationState(claimsPrincipal)));
    }

    /// <summary>
    /// Marks the user as logged out
    /// </summary>
    public async Task MarkUserAsLoggedOutAsync()
    {
        await ClearSessionAsync();
        NotifyAuthenticationStateChanged(Task.FromResult(new AuthenticationState(_anonymous)));
    }

    /// <summary>
    /// Gets the current user's JWT token
    /// </summary>
    public async Task<string?> GetTokenAsync()
    {
        try
        {
            var userSessionResult = await _sessionStorage.GetAsync<string>("UserSession");

            if (!userSessionResult.Success || string.IsNullOrEmpty(userSessionResult.Value))
            {
                return null;
            }

            var userSession = JsonSerializer.Deserialize<Models.LoginResponse>(userSessionResult.Value);
            return userSession?.Token;
        }
        catch (Exception)
        {
            return null;
        }
    }

    /// <summary>
    /// Stores the impersonated company ID for SysAdmin users.
    /// When set, the SysAdmin sees the system as if they belong to that company.
    /// Stored in protected session storage so it persists across page navigations.
    /// </summary>
    public async Task SetImpersonatedCompanyAsync(long? companyId, string? companyName)
    {
        if (companyId.HasValue)
        {
            await _sessionStorage.SetAsync("ImpersonatedCompanyId", companyId.Value);
            await _sessionStorage.SetAsync("ImpersonatedCompanyName", companyName ?? "");
        }
        else
        {
            await _sessionStorage.DeleteAsync("ImpersonatedCompanyId");
            await _sessionStorage.DeleteAsync("ImpersonatedCompanyName");
        }

        // Notify that auth state changed so UI updates (e.g., nav menu shows invoicing section)
        NotifyAuthenticationStateChanged(GetAuthenticationStateAsync());
    }

    /// <summary>
    /// Gets the currently impersonated company ID (null if not impersonating)
    /// </summary>
    public async Task<long?> GetImpersonatedCompanyIdAsync()
    {
        try
        {
            var result = await _sessionStorage.GetAsync<long>("ImpersonatedCompanyId");
            return result.Success ? result.Value : null;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// Gets the currently impersonated company name (null if not impersonating)
    /// </summary>
    public async Task<string?> GetImpersonatedCompanyNameAsync()
    {
        try
        {
            var result = await _sessionStorage.GetAsync<string>("ImpersonatedCompanyName");
            return result.Success ? result.Value : null;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// Gets the current user session
    /// </summary>
    public async Task<Models.LoginResponse?> GetUserSessionAsync()
    {
        try
        {
            var userSessionResult = await _sessionStorage.GetAsync<string>("UserSession");

            if (!userSessionResult.Success || string.IsNullOrEmpty(userSessionResult.Value))
            {
                return null;
            }

            return JsonSerializer.Deserialize<Models.LoginResponse>(userSessionResult.Value);
        }
        catch (Exception)
        {
            return null;
        }
    }

    /// <summary>
    /// Creates ClaimsPrincipal from login response
    /// </summary>
    private ClaimsPrincipal CreateClaimsPrincipal(Models.LoginResponse loginResponse)
    {
        var claims = new List<Claim>
        {
            new Claim(ClaimTypes.NameIdentifier, loginResponse.UserId.ToString()),
            new Claim(ClaimTypes.Email, loginResponse.Email),
            new Claim(ClaimTypes.Name, loginResponse.FullName),
            new Claim(ClaimTypes.Role, GetRoleName(loginResponse.Role))
        };

        if (loginResponse.CompanyId.HasValue)
        {
            claims.Add(new Claim("CompanyId", loginResponse.CompanyId.Value.ToString()));
        }

        if (!string.IsNullOrEmpty(loginResponse.CompanyName))
        {
            claims.Add(new Claim("CompanyName", loginResponse.CompanyName));
        }

        var identity = new ClaimsIdentity(claims, "apiauth");
        return new ClaimsPrincipal(identity);
    }

    /// <summary>
    /// Converts role number to role name
    /// </summary>
    private string GetRoleName(int role)
    {
        return role switch
        {
            0 => "User",
            1 => "Admin",
            2 => "SysAdmin",
            _ => "User"
        };
    }

    /// <summary>
    /// Clears the user session from storage
    /// </summary>
    private async Task ClearSessionAsync()
    {
        await _sessionStorage.DeleteAsync("UserSession");
    }
}

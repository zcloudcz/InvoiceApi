using Blazored.LocalStorage;
using Microsoft.AspNetCore.Components.Authorization;
using System.Security.Claims;
using System.Text.Json;

namespace Fakvio.UI.Shared.Services;

/// <summary>
/// Custom authentication state provider for Blazor WebAssembly.
/// Stores authentication state in browser localStorage via Blazored.LocalStorage.
///
/// Why localStorage instead of ProtectedSessionStorage?
/// ProtectedSessionStorage uses server-side Data Protection encryption, which
/// requires a running ASP.NET Core server. In standalone WASM, there is no server
/// process — the app runs entirely in the browser. Blazored.LocalStorage provides
/// a simple, browser-native key-value store that persists across page refreshes.
///
/// Security note: JWTs in localStorage are accessible to JavaScript.
/// This is acceptable because:
///   1. Our CSP prevents third-party script injection
///   2. The JWT has a short expiry time
///   3. This is the standard pattern for WASM SPAs (like Angular/React apps)
/// </summary>
public class CustomAuthenticationStateProvider : AuthenticationStateProvider
{
    // Blazored.LocalStorage service — wraps browser's localStorage API via JS interop
    private readonly ILocalStorageService _localStorage;
    private readonly AuthApiService _authApiService;

    // Anonymous principal used when no user is logged in
    private ClaimsPrincipal _anonymous = new ClaimsPrincipal(new ClaimsIdentity());

    public CustomAuthenticationStateProvider(
        ILocalStorageService localStorage,
        AuthApiService authApiService)
    {
        _localStorage = localStorage;
        _authApiService = authApiService;
    }

    /// <summary>
    /// Gets the current authentication state by reading the JWT session from localStorage.
    /// Called automatically by Blazor's CascadingAuthenticationState on every navigation.
    /// </summary>
    public override async Task<AuthenticationState> GetAuthenticationStateAsync()
    {
        try
        {
            // Read the serialized login response from localStorage
            var userSessionJson = await _localStorage.GetItemAsStringAsync("UserSession");

            if (string.IsNullOrEmpty(userSessionJson))
            {
                return new AuthenticationState(_anonymous);
            }

            var userSession = JsonSerializer.Deserialize<Models.LoginResponse>(userSessionJson);

            if (userSession == null)
            {
                return new AuthenticationState(_anonymous);
            }

            // Check if the JWT token has expired
            if (userSession.ExpiresAt <= DateTime.UtcNow)
            {
                // Token expired — clear the stale session and return anonymous
                await ClearSessionAsync();
                return new AuthenticationState(_anonymous);
            }

            // Build a ClaimsPrincipal from the stored session data
            var claimsPrincipal = CreateClaimsPrincipal(userSession);

            // If SysAdmin is impersonating a company, add the impersonated company as a claim.
            // This allows role-based checks in the UI (e.g., showing invoicing menu items).
            if (userSession.Role == 2) // SysAdmin role
            {
                try
                {
                    var impersonatedIdStr = await _localStorage.GetItemAsStringAsync("ImpersonatedCompanyId");
                    if (!string.IsNullOrEmpty(impersonatedIdStr))
                    {
                        var identity = claimsPrincipal.Identity as ClaimsIdentity;
                        identity?.AddClaim(new Claim("ImpersonatedCompanyId", impersonatedIdStr));

                        var companyName = await _localStorage.GetItemAsStringAsync("ImpersonatedCompanyName");
                        if (!string.IsNullOrEmpty(companyName))
                        {
                            identity?.AddClaim(new Claim("ImpersonatedCompanyName", companyName));
                        }
                    }
                }
                catch
                {
                    // Ignore — localStorage might not be ready yet during initial render
                }
            }

            return new AuthenticationState(claimsPrincipal);
        }
        catch (Exception)
        {
            // Any error reading localStorage → return anonymous (not logged in)
            return new AuthenticationState(_anonymous);
        }
    }

    /// <summary>
    /// Marks the user as authenticated after successful login.
    /// Stores the login response (including JWT) in localStorage.
    /// </summary>
    public async Task MarkUserAsAuthenticatedAsync(Models.LoginResponse loginResponse)
    {
        // Serialize and store the login response in localStorage
        var userSessionJson = JsonSerializer.Serialize(loginResponse);
        await _localStorage.SetItemAsStringAsync("UserSession", userSessionJson);

        // Build claims principal and notify the Blazor auth system
        var claimsPrincipal = CreateClaimsPrincipal(loginResponse);
        NotifyAuthenticationStateChanged(Task.FromResult(new AuthenticationState(claimsPrincipal)));
    }

    /// <summary>
    /// Marks the user as logged out — clears localStorage and notifies the auth system.
    /// </summary>
    public async Task MarkUserAsLoggedOutAsync()
    {
        await ClearSessionAsync();
        NotifyAuthenticationStateChanged(Task.FromResult(new AuthenticationState(_anonymous)));
    }

    /// <summary>
    /// Gets the current user's JWT token from localStorage.
    /// Used by ApiClientBase to attach the Authorization header to API requests.
    /// </summary>
    public async Task<string?> GetTokenAsync()
    {
        try
        {
            var userSessionJson = await _localStorage.GetItemAsStringAsync("UserSession");

            if (string.IsNullOrEmpty(userSessionJson))
            {
                return null;
            }

            var userSession = JsonSerializer.Deserialize<Models.LoginResponse>(userSessionJson);
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
    /// </summary>
    public async Task SetImpersonatedCompanyAsync(long? companyId, string? companyName)
    {
        if (companyId.HasValue)
        {
            await _localStorage.SetItemAsStringAsync("ImpersonatedCompanyId", companyId.Value.ToString());
            await _localStorage.SetItemAsStringAsync("ImpersonatedCompanyName", companyName ?? "");
        }
        else
        {
            await _localStorage.RemoveItemAsync("ImpersonatedCompanyId");
            await _localStorage.RemoveItemAsync("ImpersonatedCompanyName");
        }

        // Notify that auth state changed so UI updates (e.g., nav menu shows invoicing section)
        NotifyAuthenticationStateChanged(GetAuthenticationStateAsync());
    }

    /// <summary>
    /// Gets the currently impersonated company ID (null if not impersonating).
    /// </summary>
    public async Task<long?> GetImpersonatedCompanyIdAsync()
    {
        try
        {
            var idStr = await _localStorage.GetItemAsStringAsync("ImpersonatedCompanyId");
            return !string.IsNullOrEmpty(idStr) && long.TryParse(idStr, out var id) ? id : null;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// Gets the currently impersonated company name (null if not impersonating).
    /// </summary>
    public async Task<string?> GetImpersonatedCompanyNameAsync()
    {
        try
        {
            var name = await _localStorage.GetItemAsStringAsync("ImpersonatedCompanyName");
            return string.IsNullOrEmpty(name) ? null : name;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// Gets the current user session (deserialized LoginResponse) from localStorage.
    /// </summary>
    public async Task<Models.LoginResponse?> GetUserSessionAsync()
    {
        try
        {
            var userSessionJson = await _localStorage.GetItemAsStringAsync("UserSession");

            if (string.IsNullOrEmpty(userSessionJson))
            {
                return null;
            }

            return JsonSerializer.Deserialize<Models.LoginResponse>(userSessionJson);
        }
        catch (Exception)
        {
            return null;
        }
    }

    /// <summary>
    /// Creates a ClaimsPrincipal from the login response.
    /// The claims are used by Blazor's authorization system for role checks,
    /// [Authorize] attributes, and AuthorizeView components.
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

        // "apiauth" is the authentication type — any non-null value means "authenticated"
        var identity = new ClaimsIdentity(claims, "apiauth");
        return new ClaimsPrincipal(identity);
    }

    /// <summary>
    /// Converts role number to role name string for ClaimTypes.Role.
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
    /// Clears all authentication-related data from localStorage.
    /// </summary>
    private async Task ClearSessionAsync()
    {
        await _localStorage.RemoveItemAsync("UserSession");
        await _localStorage.RemoveItemAsync("ImpersonatedCompanyId");
        await _localStorage.RemoveItemAsync("ImpersonatedCompanyName");
    }
}

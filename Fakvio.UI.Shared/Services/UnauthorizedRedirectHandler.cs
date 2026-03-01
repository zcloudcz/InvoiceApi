using System.Net;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Authorization;

namespace Fakvio.UI.Shared.Services;

/// <summary>
/// HTTP message handler that intercepts 401 Unauthorized responses from the API
/// and automatically redirects the user to the login page.
///
/// This solves the global problem where expired JWT tokens caused all API calls to fail
/// silently (returning null), making every page show "Nenalezeno" instead of redirecting
/// to login. By handling 401 at the HTTP pipeline level, we don't need to add 401 checks
/// in every page or service — it's handled once, globally.
///
/// How it works:
/// 1. Every HTTP request passes through this handler (registered via AddHttpMessageHandler)
/// 2. If the API returns 401, we clear the auth state (remove token from localStorage)
/// 3. Navigate to /login so the user can re-authenticate
/// 4. Skip auth endpoints (/auth/login, /auth/register) to avoid infinite redirect loops
///
/// IMPORTANT: Dependencies (NavigationManager, AuthenticationStateProvider) are resolved
/// lazily via IServiceProvider instead of constructor injection to avoid a circular dependency.
/// The chain is: HttpClient factory → creates handler → handler needs AuthStateProvider →
/// CustomAuthenticationStateProvider needs IHttpClientFactory → creates same HttpClient → boom.
/// Lazy resolution breaks this cycle.
/// </summary>
public class UnauthorizedRedirectHandler : DelegatingHandler
{
    private readonly IServiceProvider _serviceProvider;

    /// <summary>
    /// Constructor takes IServiceProvider only — NOT NavigationManager or AuthStateProvider.
    /// This avoids circular dependency during HttpClient factory handler chain construction.
    /// </summary>
    public UnauthorizedRedirectHandler(IServiceProvider serviceProvider)
    {
        _serviceProvider = serviceProvider;
    }

    protected override async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request, CancellationToken cancellationToken)
    {
        // Let the request go through the pipeline normally
        var response = await base.SendAsync(request, cancellationToken);

        // If the API returns 401 Unauthorized, the JWT token has expired or is invalid.
        // Automatically log out and redirect to the login page.
        if (response.StatusCode == HttpStatusCode.Unauthorized)
        {
            var path = request.RequestUri?.PathAndQuery ?? "";

            // Don't redirect for auth endpoints — login/register are expected to be called
            // without a valid token. Without this check, we'd get an infinite redirect loop.
            if (!path.Contains("/auth/login", StringComparison.OrdinalIgnoreCase)
                && !path.Contains("/auth/register", StringComparison.OrdinalIgnoreCase)
                && !path.Contains("/auth/verify", StringComparison.OrdinalIgnoreCase)
                && !path.Contains("/diagnostic/", StringComparison.OrdinalIgnoreCase))
            {
                // Resolve dependencies lazily — only when a 401 actually occurs.
                // This is safe because in Blazor WASM there's a single DI scope for the app lifetime.
                var authStateProvider = _serviceProvider.GetService<AuthenticationStateProvider>();
                var customAuth = authStateProvider as CustomAuthenticationStateProvider;
                if (customAuth != null)
                {
                    await customAuth.MarkUserAsLoggedOutAsync();
                }

                var navigation = _serviceProvider.GetRequiredService<NavigationManager>();
                navigation.NavigateTo("/login", forceLoad: true);
            }
        }

        return response;
    }
}

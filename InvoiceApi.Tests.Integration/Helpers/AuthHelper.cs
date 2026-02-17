using System.Net.Http.Json;
using InvoiceApi.Contracts.Dto.Auth;

namespace InvoiceApi.Tests.Integration.Helpers;

/// <summary>
/// Helper methods for authentication in integration tests.
///
/// Provides convenient methods to:
/// - Log in as the seeded SysAdmin user (admin@zcloud.cz / Invoice123)
/// - Log in with arbitrary credentials
/// - Set the JWT Bearer token on an HttpClient
/// - Set the X-Company-Id impersonation header
///
/// The SysAdmin user is seeded by MasterDbContext.SeedData() via HasData():
/// Email: admin@zcloud.cz, Password: Invoice123, Role: SysAdmin
/// </summary>
public static class AuthHelper
{
    /// <summary>
    /// Default SysAdmin credentials (seeded in MasterDbContext).
    /// </summary>
    private const string SysAdminEmail = "admin@zcloud.cz";
    private const string SysAdminPassword = "Invoice123";

    /// <summary>
    /// Logs in as the seeded SysAdmin user and returns the LoginResponse.
    /// Throws if login fails (indicates a setup problem with the test factory).
    /// </summary>
    /// <param name="client">HttpClient created from the test factory</param>
    /// <returns>LoginResponse containing the JWT token, user ID, role, etc.</returns>
    public static async Task<LoginResponse> LoginAsSysAdminAsync(HttpClient client)
    {
        return await LoginAsync(client, SysAdminEmail, SysAdminPassword);
    }

    /// <summary>
    /// Logs in with the given email and password, returning the LoginResponse.
    /// Posts to /api/auth/login (the standard login endpoint).
    /// Throws if the response is not 200 OK.
    /// </summary>
    /// <param name="client">HttpClient created from the test factory</param>
    /// <param name="email">User email address</param>
    /// <param name="password">User password</param>
    /// <returns>LoginResponse containing the JWT token, user ID, role, etc.</returns>
    public static async Task<LoginResponse> LoginAsync(HttpClient client, string email, string password)
    {
        var loginRequest = new LoginRequest
        {
            Email = email,
            Password = password
        };

        var response = await client.PostAsJsonAsync("/api/auth/login", loginRequest);

        // Ensure login succeeded — if not, the test setup has a problem
        response.EnsureSuccessStatusCode();

        var loginResponse = await response.Content.ReadFromJsonAsync<LoginResponse>();
        return loginResponse ?? throw new InvalidOperationException(
            $"Login response was null for email: {email}");
    }

    /// <summary>
    /// Sets the Authorization header on the HttpClient with the given JWT token.
    /// All subsequent requests will include this token.
    /// </summary>
    /// <param name="client">HttpClient to configure</param>
    /// <param name="token">JWT token (without "Bearer " prefix — this method adds it)</param>
    public static void SetAuthToken(HttpClient client, string token)
    {
        // Remove any existing Authorization header before setting the new one
        client.DefaultRequestHeaders.Remove("Authorization");
        client.DefaultRequestHeaders.Add("Authorization", $"Bearer {token}");
    }

    /// <summary>
    /// Sets the X-Company-Id header for SysAdmin impersonation.
    /// This tells the ImpersonationMiddleware to add a CompanyId claim to the request,
    /// allowing SysAdmin to access tenant-scoped endpoints as if they were a user of that company.
    /// </summary>
    /// <param name="client">HttpClient to configure</param>
    /// <param name="companyId">Company ID to impersonate</param>
    public static void SetImpersonation(HttpClient client, long companyId)
    {
        // Remove any existing impersonation header before setting the new one
        client.DefaultRequestHeaders.Remove("X-Company-Id");
        client.DefaultRequestHeaders.Add("X-Company-Id", companyId.ToString());
    }

    /// <summary>
    /// Clears the impersonation header from the HttpClient.
    /// After calling this, the SysAdmin will operate without a tenant context.
    /// </summary>
    /// <param name="client">HttpClient to configure</param>
    public static void ClearImpersonation(HttpClient client)
    {
        client.DefaultRequestHeaders.Remove("X-Company-Id");
    }
}

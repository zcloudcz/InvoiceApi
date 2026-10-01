using System.ComponentModel;
using System.Text.Json;
using Fakvio.Contracts.Dto.CompanyMembership;
using Fakvio.McpServer.Client;
using ModelContextProtocol.Server;

namespace Fakvio.McpServer.Tools;

/// <summary>Company tools preserve the original credential and rely on API membership/scope checks.</summary>
[McpServerToolType]
public static class CompanyTools
{
    [McpServerTool(Name = "list_user_company_memberships", Title = "List user company memberships", ReadOnly = true, Idempotent = true, OpenWorld = false),
     Description("SysAdmin only. List a user's active and revoked company memberships. Requires read scope.")]
    public static Task<string> ListUserCompanyMemberships(IFakvioApiClient api, long userId, CancellationToken ct = default) =>
        ExecuteAsync(() => api.GetUserCompanyMembershipsAsync(userId, ct), ct);

    [McpServerTool(Name = "update_user_company_membership", Title = "Update user company membership", ReadOnly = false, Destructive = true, Idempotent = true, OpenWorld = false),
     Description("SysAdmin only, write scope. Update an existing membership's User or Admin (Accountant) role and active status. Does not change other companies, account defaults or credential grants. Invalidates pending invitations for this membership.")]
    public static Task<string> UpdateUserCompanyMembership(IFakvioApiClient api, long userId, long targetCompanyId,
        UpdateCompanyMembershipDto membership, CancellationToken ct = default) =>
        ExecuteAsync(() => api.UpdateUserCompanyMembershipAsync(userId, targetCompanyId, membership, ct), ct);

    [McpServerTool(Name = "list_companies", Title = "List accessible companies", ReadOnly = true, Idempotent = true, OpenWorld = false),
     Description("List active companies explicitly granted to this credential. Newly joined or created companies are not automatically granted. Requires read scope.")]
    public static Task<string> ListCompanies(IFakvioApiClient api, CancellationToken ct = default) =>
        ExecuteAsync(() => api.GetMyCompaniesAsync(ct), ct);

    [McpServerTool(Name = "select_company", Title = "Validate company selection", ReadOnly = true, Idempotent = true, OpenWorld = false),
     Description("Validate an allowed company for subsequent calls. No session state or token is changed: pass the returned companyId on EACH subsequent tool call. Existing API-key grants and live membership are checked; OAuth remains bound to its consent company.")]
    public static Task<string> SelectCompany(IFakvioApiClient api,
        [Description("The desired explicitly granted company ID.")] long selectedCompanyId, CancellationToken ct = default) =>
        ExecuteAsync(async () =>
        {
            if (selectedCompanyId <= 0) throw new ArgumentException("Company ID must be positive.");
            return await CompanyRequestContext.RunAsync(null, async () =>
            {
                var companies = await api.GetMyCompaniesAsync(ct);
                var company = companies.SingleOrDefault(x => x.CompanyId == selectedCompanyId && x.IsProvisioned)
                    ?? throw new UnauthorizedAccessException("Company is not available to this credential.");
                return new { companyId = company.CompanyId, company.CompanyName, company.Role,
                    instruction = "For a personal API key, pass companyId on each subsequent tool call. For OAuth, omit companyId: consent already pins the company. No token or persistent session selection was changed." };
            });
        }, ct);

    [McpServerTool(Name = "add_company", Title = "Add company", ReadOnly = false, Destructive = false, Idempotent = true, OpenWorld = false),
     Description("Create a company for your existing identity; requires write scope. Reuse the same operationId UUID when retrying. Does not grant this credential access to the new company: authorize a new key or OAuth consent in the browser before accessing it.")]
    public static Task<string> AddCompany(IFakvioApiClient api, CreateMyCompanyDto company, CancellationToken ct = default) =>
        ExecuteAsync(() => api.CreateMyCompanyAsync(company, ct), ct);

    [McpServerTool(Name = "retry_company_setup", Title = "Retry company setup", ReadOnly = false, Destructive = false, Idempotent = true, OpenWorld = false),
     Description("Retry a company creation operation owned by your identity. Requires write scope and an explicit credential grant for the target company. Use the browser for recovery before that grant is issued. Does not expand credential grants.")]
    public static Task<string> RetryCompanySetup(IFakvioApiClient api, long targetCompanyId, CancellationToken ct = default) =>
        ExecuteAsync(() => api.RetryCompanyProvisioningAsync(targetCompanyId, ct), ct);

    private static async Task<string> ExecuteAsync<T>(Func<Task<T>> operation, CancellationToken ct)
    {
        try { return JsonSerializer.Serialize(await operation(), McpToolJsonOptions.Default); }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception ex) { return McpToolError.ToJson(ex); }
    }
}

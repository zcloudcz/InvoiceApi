using Fakvio.Contracts.Dto.CompanyMembership;
using Microsoft.AspNetCore.Components.Authorization;

namespace Fakvio.UI.Shared.Services;

/// <summary>Company membership requests use the current identity; callers never supply an owner.</summary>
public sealed class CompanyMembershipApiService(IHttpClientFactory factory, ILogger<CompanyMembershipApiService> logger,
    AuthenticationStateProvider authentication) : ApiClientBase(factory, logger, authentication)
{
    public Task<List<CompanyMembershipDto>> ListAsync(CancellationToken ct = default) =>
        SendAsync<List<CompanyMembershipDto>>(HttpMethod.Get, "/api/my-companies", null, ct);
    public Task<CompanyMembershipDto> CreateAsync(CreateMyCompanyDto company, CancellationToken ct = default) =>
        SendAsync<CompanyMembershipDto>(HttpMethod.Post, "/api/my-companies", company, ct);
    public Task<CompanyMembershipDto> RetryAsync(long id, CancellationToken ct = default) =>
        SendAsync<CompanyMembershipDto>(HttpMethod.Post, $"/api/my-companies/{id}/retry-provisioning", null, ct);
    public Task<Models.LoginResponse> SwitchAsync(long companyId, CancellationToken ct = default) =>
        SendAsync<Models.LoginResponse>(HttpMethod.Post, "/api/my-companies/switch", new SwitchCompanyDto { CompanyId = companyId }, ct);
    public Task<CompanyInvitationDto> InviteAsync(InviteCompanyMemberDto invitation, CancellationToken ct = default) =>
        SendAsync<CompanyInvitationDto>(HttpMethod.Post, "/api/my-companies/invitations", invitation, ct);
    public Task<CompanyMembershipDto> AcceptAsync(string token, CancellationToken ct = default) =>
        SendAsync<CompanyMembershipDto>(HttpMethod.Post, "/api/my-companies/invitations/accept", new { Token = token }, ct);

    private async Task<T> SendAsync<T>(HttpMethod method, string endpoint, object? body, CancellationToken ct)
    {
        await AddAuthorizationHeaderAsync();
        using var request = new HttpRequestMessage(method, endpoint);
        if (body is not null) request.Content = JsonContent.Create(body);
        using var response = await _httpClient.SendAsync(request, ct);
        if (!response.IsSuccessStatusCode)
        {
            // Invitation responses and browser session responses can contain secrets.
            _logger.LogWarning("Company membership request failed: HTTP {Status}", (int)response.StatusCode);
            throw new ApiException(response.StatusCode, $"HTTP {(int)response.StatusCode}", endpoint);
        }
        return await response.Content.ReadFromJsonAsync<T>(cancellationToken: ct)
            ?? throw new HttpRequestException("Company membership response was empty.");
    }
}

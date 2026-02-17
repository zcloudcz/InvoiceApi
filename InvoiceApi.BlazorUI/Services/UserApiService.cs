using System.Text;
using InvoiceApi.Contracts.Dto.User;
using InvoiceApi.BlazorUI.Models;
using InvoiceApi.Domain.Enums;
using Microsoft.AspNetCore.Components.Authorization;

namespace InvoiceApi.BlazorUI.Services;

/// <summary>
/// Blazor service for communicating with the User API endpoints.
/// Inherits ApiClientBase for shared auth, logging, impersonation, and error handling.
/// Note: Some methods (SetPassword, ValidateInvitationToken) are anonymous endpoints —
/// they still go through the base class HttpClient but without auth headers.
/// </summary>
public class UserApiService : ApiClientBase
{
    public UserApiService(
        IHttpClientFactory httpClientFactory,
        ILogger<UserApiService> logger,
        AuthenticationStateProvider authStateProvider)
        : base(httpClientFactory, logger, authStateProvider)
    {
    }

    /// <summary>
    /// Gets all users (non-paginated).
    /// </summary>
    public async Task<List<UserDto>> GetAllAsync()
    {
        var result = await GetAsync<List<UserDto>>("/api/user");
        return result ?? new List<UserDto>();
    }

    /// <summary>
    /// Gets users with server-side pagination, filtering, and sorting.
    /// </summary>
    public async Task<PagedResult<UserDto>> GetPagedAsync(
        int page = 1,
        int pageSize = 50,
        string? search = null,
        string? sortBy = null,
        string? sortDirection = "asc",
        EUserRole? role = null,
        long? companyId = null,
        bool includeInactive = false,
        bool? neverLoggedIn = null)
    {
        var queryParams = new StringBuilder($"?Page={page}&PageSize={pageSize}");

        if (!string.IsNullOrWhiteSpace(search))
            queryParams.Append($"&Search={Uri.EscapeDataString(search)}");

        if (!string.IsNullOrWhiteSpace(sortBy))
            queryParams.Append($"&SortBy={sortBy}");

        if (!string.IsNullOrWhiteSpace(sortDirection))
            queryParams.Append($"&SortDirection={sortDirection}");

        if (role.HasValue)
            queryParams.Append($"&Role={role.Value}");

        if (companyId.HasValue)
            queryParams.Append($"&CompanyId={companyId.Value}");

        if (includeInactive)
            queryParams.Append("&IncludeInactive=true");

        if (neverLoggedIn.HasValue)
            queryParams.Append($"&NeverLoggedIn={neverLoggedIn.Value}");

        var result = await GetAsync<PagedResult<UserDto>>($"/api/user/paged{queryParams}");
        return result ?? new PagedResult<UserDto>();
    }

    /// <summary>
    /// Gets a single user by ID.
    /// </summary>
    public async Task<UserDto?> GetByIdAsync(long id)
    {
        return await GetAsync<UserDto>($"/api/user/{id}");
    }

    /// <summary>
    /// Creates a new user.
    /// </summary>
    public async Task<UserDto?> CreateAsync(CreateUserDto createDto)
    {
        return await PostAsync<CreateUserDto, UserDto>("/api/user", createDto);
    }

    /// <summary>
    /// Updates an existing user.
    /// </summary>
    public async Task<UserDto?> UpdateAsync(long id, UpdateUserDto updateDto)
    {
        return await PutAsync<UpdateUserDto, UserDto>($"/api/user/{id}", updateDto);
    }

    /// <summary>
    /// Deletes a user (soft delete).
    /// </summary>
    public async Task<bool> DeleteAsync(long id)
    {
        return await DeleteAsync($"/api/user/{id}");
    }

    /// <summary>
    /// Changes a user's password (requires current password).
    /// </summary>
    public async Task<bool> ChangePasswordAsync(long id, ChangePasswordDto changePasswordDto)
    {
        return await PutBoolAsync($"/api/user/{id}/change-password", changePasswordDto);
    }

    /// <summary>
    /// Admin password reset — sets a new password without the current one.
    /// Only Admin/SysAdmin can call this endpoint.
    /// </summary>
    public async Task<bool> AdminResetPasswordAsync(long id, AdminResetPasswordDto dto)
    {
        return await PostBoolAsync($"/api/user/{id}/admin-reset-password", dto);
    }

    /// <summary>
    /// Invites a new user by calling the /api/user/invite endpoint.
    /// Creates the user account and sends an invitation email with a password setup link.
    /// Requires Admin or SysAdmin authorization.
    /// </summary>
    public async Task<UserDto?> InviteUserAsync(InviteUserDto inviteDto)
    {
        return await PostAsync<InviteUserDto, UserDto>("/api/user/invite", inviteDto);
    }

    /// <summary>
    /// Sets password for an invited user using their invitation token.
    /// This is an anonymous call — uses the base HttpClient directly without auth header.
    /// Called from the SetPassword page when the user clicks the invitation link.
    /// </summary>
    public async Task<bool> SetPasswordAsync(SetPasswordDto setPasswordDto)
    {
        // Anonymous endpoint — call the HttpClient directly (no auth header via AddAuthorizationHeaderAsync)
        var response = await _httpClient.PostAsJsonAsync("/api/user/set-password", setPasswordDto);
        return response.IsSuccessStatusCode;
    }

    /// <summary>
    /// Validates an invitation token before showing the password form.
    /// This is an anonymous call — uses the base HttpClient directly without auth header.
    /// Returns true if the token is valid and not expired.
    /// </summary>
    public async Task<bool> ValidateInvitationTokenAsync(string token)
    {
        // Anonymous endpoint — call the HttpClient directly
        var response = await _httpClient.GetAsync($"/api/user/validate-invitation?token={Uri.EscapeDataString(token)}");
        if (!response.IsSuccessStatusCode) return false;

        var result = await response.Content.ReadFromJsonAsync<InvitationValidationResult>();
        return result?.IsValid ?? false;
    }

    /// <summary>
    /// Helper class for deserializing the invitation token validation response
    /// </summary>
    private class InvitationValidationResult
    {
        public bool IsValid { get; set; }
    }
}

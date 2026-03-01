using Fakvio.Contracts.Dto.CloudStorage;
using Fakvio.Application.Service;
using Fakvio.Domain.Enums;
using Fakvio.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using System.Net.Http.Headers;
using System.Text.Json;

namespace Fakvio.Infrastructure.Service.CloudStorage;

/// <summary>
/// OneDrive cloud storage implementation using OAuth 2.0 and Microsoft Graph REST API.
///
/// Authentication flow:
/// 1. GetAuthorizationUrlAsync → builds Microsoft identity platform authorization URL
/// 2. User approves → Microsoft redirects back with authorization code
/// 3. ExchangeCodeAsync → exchanges code for access + refresh tokens via MS token endpoint
/// 4. Tokens stored in CompanySystemSettings (OneDriveAccessToken, OneDriveRefreshToken)
/// 5. RefreshTokenAsync → uses refresh token to get new access token when expired (~1 hour)
///
/// File operations use the Microsoft Graph REST API directly via HttpClient
/// (no Microsoft.Graph SDK — keeps the dependency footprint small).
///
/// Configuration required in appsettings.json:
///   OAuth:Microsoft:ClientId — Azure AD App Registration client ID
///   OAuth:Microsoft:ClientSecret — Azure AD App Registration client secret
///   OAuth:Microsoft:TenantId — "common" for multi-tenant or specific tenant ID
/// </summary>
public class OneDriveStorageService : IExternalCloudStorage
{
    private readonly MasterDbContext _masterContext;
    private readonly ITenantResolver _tenantResolver;
    private readonly IConfiguration _configuration;
    private readonly ILogger<OneDriveStorageService> _logger;
    private readonly HttpClient _httpClient;

    // Microsoft identity platform endpoints
    private const string GraphApiBase = "https://graph.microsoft.com/v1.0";
    private const string Scope = "Files.ReadWrite offline_access";

    /// <inheritdoc />
    public ECloudStorageProvider Provider => ECloudStorageProvider.OneDrive;

    public OneDriveStorageService(
        MasterDbContext masterContext,
        ITenantResolver tenantResolver,
        IConfiguration configuration,
        ILogger<OneDriveStorageService> logger,
        HttpClient httpClient)
    {
        _masterContext = masterContext;
        _tenantResolver = tenantResolver;
        _configuration = configuration;
        _logger = logger;
        _httpClient = httpClient;
    }

    /// <summary>Microsoft OAuth client ID from configuration.</summary>
    private string ClientId => _configuration["OAuth:Microsoft:ClientId"]
        ?? throw new InvalidOperationException("OAuth:Microsoft:ClientId not configured.");

    /// <summary>Microsoft OAuth client secret from configuration.</summary>
    private string ClientSecret => _configuration["OAuth:Microsoft:ClientSecret"]
        ?? throw new InvalidOperationException("OAuth:Microsoft:ClientSecret not configured.");

    /// <summary>Azure AD tenant ID — "common" for multi-tenant apps.</summary>
    private string TenantId => _configuration["OAuth:Microsoft:TenantId"] ?? "common";

    /// <summary>Microsoft authorization endpoint URL.</summary>
    private string AuthorizationEndpoint => $"https://login.microsoftonline.com/{TenantId}/oauth2/v2.0/authorize";

    /// <summary>Microsoft token endpoint URL.</summary>
    private string TokenEndpoint => $"https://login.microsoftonline.com/{TenantId}/oauth2/v2.0/token";

    /// <inheritdoc />
    public Task<string> GetAuthorizationUrlAsync(string redirectUri, CancellationToken ct = default)
    {
        // Build Microsoft identity platform authorization URL.
        // response_mode=query returns the code as a URL query parameter.
        var url = $"{AuthorizationEndpoint}" +
                  $"?client_id={Uri.EscapeDataString(ClientId)}" +
                  $"&redirect_uri={Uri.EscapeDataString(redirectUri)}" +
                  $"&response_type=code" +
                  $"&scope={Uri.EscapeDataString(Scope)}" +
                  $"&response_mode=query";

        return Task.FromResult(url);
    }

    /// <inheritdoc />
    public async Task<bool> ExchangeCodeAsync(string code, string redirectUri, CancellationToken ct = default)
    {
        _logger.LogInformation("Exchanging OneDrive authorization code for tokens");

        var requestBody = new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["code"] = code,
            ["client_id"] = ClientId,
            ["client_secret"] = ClientSecret,
            ["redirect_uri"] = redirectUri,
            ["grant_type"] = "authorization_code",
            ["scope"] = Scope
        });

        var response = await _httpClient.PostAsync(TokenEndpoint, requestBody, ct);
        if (!response.IsSuccessStatusCode)
        {
            var error = await response.Content.ReadAsStringAsync(ct);
            _logger.LogWarning("OneDrive token exchange failed: {StatusCode} {Error}",
                response.StatusCode, error);
            return false;
        }

        var json = await response.Content.ReadAsStringAsync(ct);
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        var accessToken = root.GetProperty("access_token").GetString()!;
        var refreshToken = root.TryGetProperty("refresh_token", out var rt) ? rt.GetString() : null;
        var expiresIn = root.GetProperty("expires_in").GetInt32();

        await SaveTokensAsync(accessToken, refreshToken, expiresIn, ct);

        _logger.LogInformation("OneDrive connected successfully");
        return true;
    }

    /// <inheritdoc />
    public async Task<bool> RefreshTokenAsync(CancellationToken ct = default)
    {
        var settings = await GetCompanySettingsAsync(ct);
        if (string.IsNullOrEmpty(settings?.OneDriveRefreshToken))
        {
            _logger.LogWarning("No OneDrive refresh token available");
            return false;
        }

        var requestBody = new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["client_id"] = ClientId,
            ["client_secret"] = ClientSecret,
            ["refresh_token"] = settings.OneDriveRefreshToken,
            ["grant_type"] = "refresh_token",
            ["scope"] = Scope
        });

        var response = await _httpClient.PostAsync(TokenEndpoint, requestBody, ct);
        if (!response.IsSuccessStatusCode)
        {
            _logger.LogWarning("OneDrive token refresh failed: {StatusCode}", response.StatusCode);
            return false;
        }

        var json = await response.Content.ReadAsStringAsync(ct);
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        var accessToken = root.GetProperty("access_token").GetString()!;
        // Microsoft may return a new refresh token — always update if present
        var newRefreshToken = root.TryGetProperty("refresh_token", out var nrt) ? nrt.GetString() : null;
        var expiresIn = root.GetProperty("expires_in").GetInt32();

        await SaveTokensAsync(accessToken, newRefreshToken, expiresIn, ct);

        _logger.LogInformation("OneDrive access token refreshed successfully");
        return true;
    }

    /// <inheritdoc />
    public async Task<CloudStorageFileResult> UploadFileAsync(
        string fileName, byte[] content, string contentType, CancellationToken ct = default)
    {
        await EnsureValidTokenAsync(ct);

        var settings = await GetCompanySettingsAsync(ct);
        var accessToken = settings?.OneDriveAccessToken
            ?? throw new InvalidOperationException("OneDrive access token not available.");

        // Build the upload URL — if a folder is selected, upload to that folder; otherwise root
        string uploadUrl;
        if (!string.IsNullOrEmpty(settings.OneDriveFolderId))
        {
            // Upload to a specific folder by item ID
            uploadUrl = $"{GraphApiBase}/me/drive/items/{settings.OneDriveFolderId}:/{Uri.EscapeDataString(fileName)}:/content";
        }
        else
        {
            // Upload to OneDrive root
            uploadUrl = $"{GraphApiBase}/me/drive/root:/{Uri.EscapeDataString(fileName)}:/content";
        }

        // Simple PUT upload (for files < 4MB — PDFs are typically well under this)
        using var request = new HttpRequestMessage(HttpMethod.Put, uploadUrl);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
        request.Content = new ByteArrayContent(content);
        request.Content.Headers.ContentType = new MediaTypeHeaderValue(contentType);

        var response = await _httpClient.SendAsync(request, ct);

        if (!response.IsSuccessStatusCode)
        {
            var error = await response.Content.ReadAsStringAsync(ct);
            _logger.LogError("OneDrive upload failed: {StatusCode} {Error}", response.StatusCode, error);
            throw new InvalidOperationException($"OneDrive upload failed: {response.StatusCode}");
        }

        var json = await response.Content.ReadAsStringAsync(ct);
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        _logger.LogInformation("File '{FileName}' uploaded to OneDrive successfully", fileName);

        return new CloudStorageFileResult
        {
            FileName = fileName,
            FileId = root.GetProperty("id").GetString() ?? string.Empty,
            Url = root.TryGetProperty("webUrl", out var webUrl) ? webUrl.GetString() : null
        };
    }

    /// <inheritdoc />
    public async Task<List<CloudStorageFolderDto>> ListFoldersAsync(
        string? parentId, CancellationToken ct = default)
    {
        await EnsureValidTokenAsync(ct);

        var settings = await GetCompanySettingsAsync(ct);
        var accessToken = settings?.OneDriveAccessToken
            ?? throw new InvalidOperationException("OneDrive access token not available.");

        // Build Graph API URL for listing children of a folder
        // Filter to folders only using the 'folder' facet
        string listUrl;
        if (string.IsNullOrEmpty(parentId))
        {
            listUrl = $"{GraphApiBase}/me/drive/root/children?$filter=folder ne null&$select=id,name,parentReference&$orderby=name";
        }
        else
        {
            listUrl = $"{GraphApiBase}/me/drive/items/{parentId}/children?$filter=folder ne null&$select=id,name,parentReference&$orderby=name";
        }

        using var request = new HttpRequestMessage(HttpMethod.Get, listUrl);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);

        var response = await _httpClient.SendAsync(request, ct);
        if (!response.IsSuccessStatusCode)
        {
            _logger.LogWarning("OneDrive list folders failed: {StatusCode}", response.StatusCode);
            return new List<CloudStorageFolderDto>();
        }

        var json = await response.Content.ReadAsStringAsync(ct);
        using var doc = JsonDocument.Parse(json);

        var folders = new List<CloudStorageFolderDto>();
        if (doc.RootElement.TryGetProperty("value", out var items))
        {
            foreach (var item in items.EnumerateArray())
            {
                // Only include items that have the "folder" facet
                if (!item.TryGetProperty("folder", out _)) continue;

                folders.Add(new CloudStorageFolderDto
                {
                    Id = item.GetProperty("id").GetString() ?? string.Empty,
                    Name = item.GetProperty("name").GetString() ?? string.Empty,
                    ParentId = parentId
                });
            }
        }

        return folders;
    }

    /// <inheritdoc />
    public async Task<bool> TestConnectionAsync(CancellationToken ct = default)
    {
        try
        {
            await EnsureValidTokenAsync(ct);
            var settings = await GetCompanySettingsAsync(ct);
            if (string.IsNullOrEmpty(settings?.OneDriveAccessToken))
                return false;

            // Lightweight test: get root drive metadata
            using var request = new HttpRequestMessage(HttpMethod.Get, $"{GraphApiBase}/me/drive");
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", settings.OneDriveAccessToken);

            var response = await _httpClient.SendAsync(request, ct);
            return response.IsSuccessStatusCode;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "OneDrive connection test failed");
            return false;
        }
    }

    /// <inheritdoc />
    public async Task<bool> DisconnectAsync(CancellationToken ct = default)
    {
        var settings = await GetCompanySettingsAsync(ct);
        if (settings == null) return false;

        // Clear all OneDrive credentials from CompanySystemSettings
        // Microsoft doesn't have a simple token revocation endpoint like Google,
        // so we just clear the stored tokens.
        settings.OneDriveEnabled = false;
        settings.OneDriveAccessToken = null;
        settings.OneDriveRefreshToken = null;
        settings.OneDriveTokenExpiresAt = null;
        settings.OneDriveFolderId = null;
        settings.OneDriveFolderName = null;

        await _masterContext.SaveChangesAsync(ct);

        _logger.LogInformation("OneDrive disconnected for company {CompanyId}",
            _tenantResolver.GetCurrentCompanyId());
        return true;
    }

    // ─── Private Helpers ─────────────────────────────────────────────────────

    /// <summary>
    /// Gets the CompanySystemSettings for the current company from the master database.
    /// </summary>
    private async Task<Domain.Entities.CompanySystemSettings?> GetCompanySettingsAsync(CancellationToken ct)
    {
        var companyId = _tenantResolver.GetCurrentCompanyId()
            ?? throw new InvalidOperationException("No company context — cannot access cloud storage settings.");

        return await _masterContext.CompanySystemSettings
            .FirstOrDefaultAsync(s => s.CompanyId == companyId, ct);
    }

    /// <summary>
    /// Saves OAuth tokens to CompanySystemSettings and enables OneDrive.
    /// </summary>
    private async Task SaveTokensAsync(
        string accessToken, string? refreshToken, int expiresInSeconds, CancellationToken ct)
    {
        var companyId = _tenantResolver.GetCurrentCompanyId()
            ?? throw new InvalidOperationException("No company context.");

        var settings = await _masterContext.CompanySystemSettings
            .FirstOrDefaultAsync(s => s.CompanyId == companyId, ct)
            ?? throw new InvalidOperationException($"CompanySystemSettings not found for company {companyId}.");

        settings.OneDriveEnabled = true;
        settings.OneDriveAccessToken = accessToken;
        settings.OneDriveTokenExpiresAt = DateTime.UtcNow.AddSeconds(expiresInSeconds - 60); // 60s buffer

        if (!string.IsNullOrEmpty(refreshToken))
        {
            settings.OneDriveRefreshToken = refreshToken;
        }

        await _masterContext.SaveChangesAsync(ct);
    }

    /// <summary>
    /// Ensures the access token is valid; refreshes it if expired.
    /// </summary>
    private async Task EnsureValidTokenAsync(CancellationToken ct)
    {
        var settings = await GetCompanySettingsAsync(ct);
        if (settings?.OneDriveTokenExpiresAt != null &&
            settings.OneDriveTokenExpiresAt <= DateTime.UtcNow)
        {
            _logger.LogInformation("OneDrive token expired, refreshing...");
            var refreshed = await RefreshTokenAsync(ct);
            if (!refreshed)
            {
                throw new InvalidOperationException("Failed to refresh OneDrive access token.");
            }
        }
    }
}

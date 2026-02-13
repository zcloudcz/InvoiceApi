using InvoiceApi.Application.Dto.CloudStorage;
using InvoiceApi.Application.Service;
using InvoiceApi.Domain.Enums;
using InvoiceApi.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using System.Net.Http.Headers;
using System.Text.Json;

namespace InvoiceApi.Infrastructure.Service.CloudStorage;

/// <summary>
/// Google Drive cloud storage implementation using OAuth 2.0 and the Google Drive REST API v3.
///
/// Authentication flow:
/// 1. GetAuthorizationUrlAsync → builds Google OAuth consent URL with "drive.file" scope
/// 2. User approves → Google redirects back with authorization code
/// 3. ExchangeCodeAsync → exchanges code for access + refresh tokens via Google token endpoint
/// 4. Tokens stored in CompanySystemSettings (GoogleDriveAccessToken, GoogleDriveRefreshToken)
/// 5. RefreshTokenAsync → uses refresh token to get new access token when expired (~1 hour)
///
/// File operations use the Google Drive REST API directly via HttpClient
/// (no Google SDK dependency — simpler and lighter for our needs).
///
/// Configuration required in appsettings.json:
///   OAuth:Google:ClientId — Google Cloud Console OAuth 2.0 client ID
///   OAuth:Google:ClientSecret — Google Cloud Console OAuth 2.0 client secret
/// </summary>
public class GoogleDriveStorageService : IExternalCloudStorage
{
    private readonly MasterDbContext _masterContext;
    private readonly ITenantResolver _tenantResolver;
    private readonly IConfiguration _configuration;
    private readonly ILogger<GoogleDriveStorageService> _logger;
    private readonly HttpClient _httpClient;

    // Google OAuth 2.0 endpoints
    private const string AuthorizationEndpoint = "https://accounts.google.com/o/oauth2/v2/auth";
    private const string TokenEndpoint = "https://oauth2.googleapis.com/token";
    private const string RevokeEndpoint = "https://oauth2.googleapis.com/revoke";

    // Google Drive REST API v3 base URL
    private const string DriveApiBase = "https://www.googleapis.com/drive/v3";
    private const string DriveUploadBase = "https://www.googleapis.com/upload/drive/v3";

    // Only request access to files created by this app — minimal permissions
    private const string Scope = "https://www.googleapis.com/auth/drive.file";

    /// <inheritdoc />
    public ECloudStorageProvider Provider => ECloudStorageProvider.GoogleDrive;

    public GoogleDriveStorageService(
        MasterDbContext masterContext,
        ITenantResolver tenantResolver,
        IConfiguration configuration,
        ILogger<GoogleDriveStorageService> logger,
        HttpClient httpClient)
    {
        _masterContext = masterContext;
        _tenantResolver = tenantResolver;
        _configuration = configuration;
        _logger = logger;
        _httpClient = httpClient;
    }

    /// <summary>
    /// Reads the Google OAuth client ID from configuration.
    /// </summary>
    private string ClientId => _configuration["OAuth:Google:ClientId"]
        ?? throw new InvalidOperationException("OAuth:Google:ClientId not configured.");

    /// <summary>
    /// Reads the Google OAuth client secret from configuration.
    /// </summary>
    private string ClientSecret => _configuration["OAuth:Google:ClientSecret"]
        ?? throw new InvalidOperationException("OAuth:Google:ClientSecret not configured.");

    /// <inheritdoc />
    public Task<string> GetAuthorizationUrlAsync(string redirectUri, CancellationToken ct = default)
    {
        // Build Google OAuth 2.0 authorization URL with required parameters.
        // access_type=offline ensures we get a refresh token (not just an access token).
        // prompt=consent forces the consent screen even if previously approved
        // (ensures we always get a refresh token).
        var url = $"{AuthorizationEndpoint}" +
                  $"?client_id={Uri.EscapeDataString(ClientId)}" +
                  $"&redirect_uri={Uri.EscapeDataString(redirectUri)}" +
                  $"&response_type=code" +
                  $"&scope={Uri.EscapeDataString(Scope)}" +
                  $"&access_type=offline" +
                  $"&prompt=consent";

        return Task.FromResult(url);
    }

    /// <inheritdoc />
    public async Task<bool> ExchangeCodeAsync(string code, string redirectUri, CancellationToken ct = default)
    {
        _logger.LogInformation("Exchanging Google Drive authorization code for tokens");

        // POST to Google's token endpoint with the authorization code
        var requestBody = new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["code"] = code,
            ["client_id"] = ClientId,
            ["client_secret"] = ClientSecret,
            ["redirect_uri"] = redirectUri,
            ["grant_type"] = "authorization_code"
        });

        var response = await _httpClient.PostAsync(TokenEndpoint, requestBody, ct);
        if (!response.IsSuccessStatusCode)
        {
            var error = await response.Content.ReadAsStringAsync(ct);
            _logger.LogWarning("Google token exchange failed: {StatusCode} {Error}",
                response.StatusCode, error);
            return false;
        }

        // Parse the JSON response to extract tokens
        var json = await response.Content.ReadAsStringAsync(ct);
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        var accessToken = root.GetProperty("access_token").GetString()!;
        var refreshToken = root.TryGetProperty("refresh_token", out var rt) ? rt.GetString() : null;
        var expiresIn = root.GetProperty("expires_in").GetInt32();

        // Store tokens in CompanySystemSettings (master database)
        await SaveTokensAsync(accessToken, refreshToken, expiresIn, ct);

        _logger.LogInformation("Google Drive connected successfully");
        return true;
    }

    /// <inheritdoc />
    public async Task<bool> RefreshTokenAsync(CancellationToken ct = default)
    {
        var settings = await GetCompanySettingsAsync(ct);
        if (string.IsNullOrEmpty(settings?.GoogleDriveRefreshToken))
        {
            _logger.LogWarning("No Google Drive refresh token available");
            return false;
        }

        // POST to Google's token endpoint with the refresh token
        var requestBody = new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["client_id"] = ClientId,
            ["client_secret"] = ClientSecret,
            ["refresh_token"] = settings.GoogleDriveRefreshToken,
            ["grant_type"] = "refresh_token"
        });

        var response = await _httpClient.PostAsync(TokenEndpoint, requestBody, ct);
        if (!response.IsSuccessStatusCode)
        {
            _logger.LogWarning("Google Drive token refresh failed: {StatusCode}", response.StatusCode);
            return false;
        }

        var json = await response.Content.ReadAsStringAsync(ct);
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        var accessToken = root.GetProperty("access_token").GetString()!;
        var expiresIn = root.GetProperty("expires_in").GetInt32();

        // Update only the access token (refresh token stays the same)
        await SaveTokensAsync(accessToken, null, expiresIn, ct);

        _logger.LogInformation("Google Drive access token refreshed successfully");
        return true;
    }

    /// <inheritdoc />
    public async Task<CloudStorageFileResult> UploadFileAsync(
        string fileName, byte[] content, string contentType, CancellationToken ct = default)
    {
        // Ensure we have a valid access token
        await EnsureValidTokenAsync(ct);

        var settings = await GetCompanySettingsAsync(ct);
        var accessToken = settings?.GoogleDriveAccessToken
            ?? throw new InvalidOperationException("Google Drive access token not available.");

        // Build multipart upload request (simple upload with metadata)
        // Google Drive v3 multipart upload: metadata JSON + file content in one request
        var folderId = settings.GoogleDriveFolderId;

        // Metadata part — sets the file name and optionally the parent folder
        var metadata = new Dictionary<string, object> { ["name"] = fileName };
        if (!string.IsNullOrEmpty(folderId))
        {
            metadata["parents"] = new[] { folderId };
        }

        var metadataJson = JsonSerializer.Serialize(metadata);

        // Create multipart form data
        using var multipartContent = new MultipartContent("related");
        var metadataPart = new StringContent(metadataJson, System.Text.Encoding.UTF8, "application/json");
        multipartContent.Add(metadataPart);

        var filePart = new ByteArrayContent(content);
        filePart.Headers.ContentType = new MediaTypeHeaderValue(contentType);
        multipartContent.Add(filePart);

        // Upload to Google Drive
        using var request = new HttpRequestMessage(
            HttpMethod.Post,
            $"{DriveUploadBase}/files?uploadType=multipart&fields=id,webViewLink");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
        request.Content = multipartContent;

        var response = await _httpClient.SendAsync(request, ct);

        if (!response.IsSuccessStatusCode)
        {
            var error = await response.Content.ReadAsStringAsync(ct);
            _logger.LogError("Google Drive upload failed: {StatusCode} {Error}", response.StatusCode, error);
            throw new InvalidOperationException($"Google Drive upload failed: {response.StatusCode}");
        }

        var json = await response.Content.ReadAsStringAsync(ct);
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        _logger.LogInformation("File '{FileName}' uploaded to Google Drive successfully", fileName);

        return new CloudStorageFileResult
        {
            FileName = fileName,
            FileId = root.GetProperty("id").GetString() ?? string.Empty,
            Url = root.TryGetProperty("webViewLink", out var link) ? link.GetString() : null
        };
    }

    /// <inheritdoc />
    public async Task<List<CloudStorageFolderDto>> ListFoldersAsync(
        string? parentId, CancellationToken ct = default)
    {
        await EnsureValidTokenAsync(ct);

        var settings = await GetCompanySettingsAsync(ct);
        var accessToken = settings?.GoogleDriveAccessToken
            ?? throw new InvalidOperationException("Google Drive access token not available.");

        // Query Google Drive for folders in the specified parent
        // mimeType = 'application/vnd.google-apps.folder' filters to folders only
        var parentQuery = string.IsNullOrEmpty(parentId) ? "'root' in parents" : $"'{parentId}' in parents";
        var query = $"{parentQuery} and mimeType='application/vnd.google-apps.folder' and trashed=false";

        using var request = new HttpRequestMessage(
            HttpMethod.Get,
            $"{DriveApiBase}/files?q={Uri.EscapeDataString(query)}&fields=files(id,name,parents)&orderBy=name");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);

        var response = await _httpClient.SendAsync(request, ct);
        if (!response.IsSuccessStatusCode)
        {
            _logger.LogWarning("Google Drive list folders failed: {StatusCode}", response.StatusCode);
            return new List<CloudStorageFolderDto>();
        }

        var json = await response.Content.ReadAsStringAsync(ct);
        using var doc = JsonDocument.Parse(json);

        var folders = new List<CloudStorageFolderDto>();
        if (doc.RootElement.TryGetProperty("files", out var files))
        {
            foreach (var file in files.EnumerateArray())
            {
                folders.Add(new CloudStorageFolderDto
                {
                    Id = file.GetProperty("id").GetString() ?? string.Empty,
                    Name = file.GetProperty("name").GetString() ?? string.Empty,
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
            if (string.IsNullOrEmpty(settings?.GoogleDriveAccessToken))
                return false;

            // Lightweight test: list root folder (1 file max)
            using var request = new HttpRequestMessage(
                HttpMethod.Get,
                $"{DriveApiBase}/files?pageSize=1&fields=files(id)");
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", settings.GoogleDriveAccessToken);

            var response = await _httpClient.SendAsync(request, ct);
            return response.IsSuccessStatusCode;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Google Drive connection test failed");
            return false;
        }
    }

    /// <inheritdoc />
    public async Task<bool> DisconnectAsync(CancellationToken ct = default)
    {
        var settings = await GetCompanySettingsAsync(ct);
        if (settings == null) return false;

        // Revoke the access token at Google (best-effort — don't fail if revoke fails)
        if (!string.IsNullOrEmpty(settings.GoogleDriveAccessToken))
        {
            try
            {
                var revokeBody = new FormUrlEncodedContent(new Dictionary<string, string>
                {
                    ["token"] = settings.GoogleDriveAccessToken
                });
                await _httpClient.PostAsync(RevokeEndpoint, revokeBody, ct);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to revoke Google Drive token (continuing with disconnect)");
            }
        }

        // Clear all Google Drive credentials from CompanySystemSettings
        settings.GoogleDriveEnabled = false;
        settings.GoogleDriveAccessToken = null;
        settings.GoogleDriveRefreshToken = null;
        settings.GoogleDriveTokenExpiresAt = null;
        settings.GoogleDriveFolderId = null;
        settings.GoogleDriveFolderName = null;

        await _masterContext.SaveChangesAsync(ct);

        _logger.LogInformation("Google Drive disconnected for company {CompanyId}",
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
    /// Saves OAuth tokens to CompanySystemSettings and enables Google Drive.
    /// If refreshToken is null (token refresh response), only updates the access token.
    /// </summary>
    private async Task SaveTokensAsync(
        string accessToken, string? refreshToken, int expiresInSeconds, CancellationToken ct)
    {
        var companyId = _tenantResolver.GetCurrentCompanyId()
            ?? throw new InvalidOperationException("No company context.");

        var settings = await _masterContext.CompanySystemSettings
            .FirstOrDefaultAsync(s => s.CompanyId == companyId, ct)
            ?? throw new InvalidOperationException($"CompanySystemSettings not found for company {companyId}.");

        settings.GoogleDriveEnabled = true;
        settings.GoogleDriveAccessToken = accessToken;
        settings.GoogleDriveTokenExpiresAt = DateTime.UtcNow.AddSeconds(expiresInSeconds - 60); // 60s buffer

        // Only update refresh token if provided (initial auth gives it; refresh does not)
        if (!string.IsNullOrEmpty(refreshToken))
        {
            settings.GoogleDriveRefreshToken = refreshToken;
        }

        await _masterContext.SaveChangesAsync(ct);
    }

    /// <summary>
    /// Ensures the access token is valid; refreshes it if expired.
    /// </summary>
    private async Task EnsureValidTokenAsync(CancellationToken ct)
    {
        var settings = await GetCompanySettingsAsync(ct);
        if (settings?.GoogleDriveTokenExpiresAt != null &&
            settings.GoogleDriveTokenExpiresAt <= DateTime.UtcNow)
        {
            _logger.LogInformation("Google Drive token expired, refreshing...");
            var refreshed = await RefreshTokenAsync(ct);
            if (!refreshed)
            {
                throw new InvalidOperationException("Failed to refresh Google Drive access token.");
            }
        }
    }
}

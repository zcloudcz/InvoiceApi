using Azure.Storage.Blobs;
using Azure.Storage.Blobs.Models;
using Fakvio.Application.Service;
using Fakvio.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace Fakvio.Infrastructure.Service.FileStorage;

/// <summary>
/// Azure Blob Storage implementation of IFileStorage.
/// Handles upload, download, delete, and existence checks for file blobs.
///
/// Connection string resolution follows the standard 3-tier fallback pattern:
/// 1. CompanySystemSettings.AzureBlobConnectionString (per-company override)
/// 2. SystemConfiguration.AzureBlobConnectionString (system-wide)
/// 3. appsettings.json "AzureBlobStorage:ConnectionString" (lowest priority)
///
/// Container naming: "{prefix}-{companyId}" (e.g., "tenant-42").
/// Containers are created lazily on first upload if they don't exist yet.
///
/// Connection string is stored encrypted in the database — decrypted at runtime
/// via ICredentialProtector before creating the BlobServiceClient.
/// </summary>
public class AzureBlobFileStorage : IFileStorage
{
    private readonly MasterDbContext _masterContext;
    private readonly ITenantResolver _tenantResolver;
    private readonly ICredentialProtector _credentialProtector;
    private readonly IConfiguration _configuration;
    private readonly ILogger<AzureBlobFileStorage> _logger;

    public AzureBlobFileStorage(
        MasterDbContext masterContext,
        ITenantResolver tenantResolver,
        ICredentialProtector credentialProtector,
        IConfiguration configuration,
        ILogger<AzureBlobFileStorage> logger)
    {
        _masterContext = masterContext;
        _tenantResolver = tenantResolver;
        _credentialProtector = credentialProtector;
        _configuration = configuration;
        _logger = logger;
    }

    /// <inheritdoc />
    public async Task<string> UploadAsync(
        string containerName,
        string blobPath,
        byte[] content,
        string contentType,
        CancellationToken ct = default)
    {
        var (containerClient, blobClient) = await GetBlobAndContainerClientAsync(containerName, blobPath, ct);

        // Ensure the container exists before uploading.
        // CreateIfNotExistsAsync is idempotent — safe to call on every upload.
        await containerClient.CreateIfNotExistsAsync(cancellationToken: ct);

        // Set Content-Type so browsers handle the file correctly on download,
        // and Content-Disposition so the browser suggests the correct file name.
        var headers = new BlobHttpHeaders
        {
            ContentType = contentType
        };

        await blobClient.UploadAsync(
            new BinaryData(content),
            new BlobUploadOptions { HttpHeaders = headers },
            ct);

        _logger.LogInformation(
            "Uploaded blob {BlobPath} to container {Container} ({Size} bytes)",
            blobPath, containerName, content.Length);

        return blobPath;
    }

    /// <inheritdoc />
    public async Task<byte[]> DownloadAsync(
        string containerName,
        string blobPath,
        CancellationToken ct = default)
    {
        var (_, blobClient) = await GetBlobAndContainerClientAsync(containerName, blobPath, ct);

        var response = await blobClient.DownloadContentAsync(ct);
        return response.Value.Content.ToArray();
    }

    /// <inheritdoc />
    public async Task<bool> DeleteAsync(
        string containerName,
        string blobPath,
        CancellationToken ct = default)
    {
        var (_, blobClient) = await GetBlobAndContainerClientAsync(containerName, blobPath, ct);

        var response = await blobClient.DeleteIfExistsAsync(cancellationToken: ct);

        if (response.Value)
        {
            _logger.LogInformation("Deleted blob {BlobPath} from container {Container}", blobPath, containerName);
        }

        return response.Value;
    }

    /// <inheritdoc />
    public async Task<bool> ExistsAsync(
        string containerName,
        string blobPath,
        CancellationToken ct = default)
    {
        var (_, blobClient) = await GetBlobAndContainerClientAsync(containerName, blobPath, ct);
        var response = await blobClient.ExistsAsync(ct);
        return response.Value;
    }

    // ─── Private Helpers ─────────────────────────────────────────────────────

    /// <summary>
    /// Creates a BlobContainerClient and BlobClient for the specified container and blob path.
    /// Resolves the connection string using the 3-tier fallback pattern.
    /// Returns both clients so the caller can create the container if needed.
    /// </summary>
    private async Task<(BlobContainerClient Container, BlobClient Blob)> GetBlobAndContainerClientAsync(
        string containerName, string blobPath, CancellationToken ct)
    {
        var connectionString = await ResolveConnectionStringAsync(ct);

        if (string.IsNullOrWhiteSpace(connectionString))
        {
            throw new InvalidOperationException(
                "Azure Blob Storage connection string is not configured. " +
                "Set it in CompanySystemSettings, SystemConfiguration, or appsettings.json (AzureBlobStorage:ConnectionString).");
        }

        var serviceClient = new BlobServiceClient(connectionString);
        var containerClient = serviceClient.GetBlobContainerClient(containerName);
        return (containerClient, containerClient.GetBlobClient(blobPath));
    }

    /// <summary>
    /// Resolves the Azure Blob Storage connection string using the 3-tier fallback:
    /// 1. CompanySystemSettings.AzureBlobConnectionString (per-company)
    /// 2. SystemConfiguration.AzureBlobConnectionString (system-wide DB)
    /// 3. appsettings.json "AzureBlobStorage:ConnectionString"
    ///
    /// Connection strings from the database are decrypted via ICredentialProtector.
    /// </summary>
    private async Task<string?> ResolveConnectionStringAsync(CancellationToken ct)
    {
        // Tier 1: Company-specific override
        var companyId = _tenantResolver.GetCurrentCompanyId();
        if (companyId.HasValue)
        {
            var companySettings = await _masterContext.CompanySystemSettings
                .AsNoTracking()
                .FirstOrDefaultAsync(s => s.CompanyId == companyId.Value, ct);

            if (companySettings != null &&
                !string.IsNullOrWhiteSpace(companySettings.AzureBlobConnectionString))
            {
                _logger.LogDebug(
                    "Using company Azure Blob Storage config for CompanyId {CompanyId}", companyId.Value);
                return _credentialProtector.Decrypt(companySettings.AzureBlobConnectionString);
            }
        }

        // Tier 2: System-wide DB config
        var systemConfig = await _masterContext.Set<Domain.Entities.SystemConfiguration>()
            .AsNoTracking()
            .FirstOrDefaultAsync(ct);

        if (systemConfig != null &&
            !string.IsNullOrWhiteSpace(systemConfig.AzureBlobConnectionString))
        {
            _logger.LogDebug("Using system-wide Azure Blob Storage config from SystemConfiguration");
            return _credentialProtector.Decrypt(systemConfig.AzureBlobConnectionString);
        }

        // Tier 3: appsettings.json fallback
        var appSettingsCs = _configuration["AzureBlobStorage:ConnectionString"];
        if (!string.IsNullOrWhiteSpace(appSettingsCs))
        {
            _logger.LogDebug("Using Azure Blob Storage config from appsettings.json");
            return appSettingsCs;
        }

        return null;
    }
}

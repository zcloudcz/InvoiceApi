using Azure.Storage.Blobs;
using Azure.Storage.Blobs.Models;
using Fakvio.Application.Service;
using Fakvio.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace Fakvio.Infrastructure.Service.FileStorage;

/// <summary>
/// Azure Blob Storage implementation of <see cref="IFileStorage"/>.
/// Handles upload, download, delete, and existence checks for file blobs.
///
/// Architecture: ONE shared blob container (default "fakvio-files") holds
/// all tenant attachments. Each tenant gets a top-level directory inside
/// (e.g., blob path "42/a1b2c3d4.pdf" lives in container "fakvio-files"
/// under the directory "42/").
///
/// Connection string AND container name both follow the same 3-tier fallback:
/// 1. CompanySystemSettings.AzureBlob[ConnectionString|ContainerName] (per-company)
/// 2. SystemConfiguration.AzureBlob[ConnectionString|ContainerName]   (system-wide)
/// 3. appsettings.json "AzureBlobStorage:[ConnectionString|ContainerName]"
/// 4. Hard-coded default container name "fakvio-files"
///
/// Connection string from the database is encrypted at rest via
/// <see cref="ICredentialProtector"/> and decrypted on read.
/// </summary>
public class AzureBlobFileStorage : IFileStorage
{
    private readonly MasterDbContext _masterContext;
    private readonly ITenantResolver _tenantResolver;
    private readonly ICredentialProtector _credentialProtector;
    private readonly IConfiguration _configuration;
    private readonly ILogger<AzureBlobFileStorage> _logger;

    /// <summary>
    /// Hard-coded default container name when nothing is configured anywhere.
    /// Lowercase, 3–63 chars, only letters/digits/hyphens — Azure container naming rules.
    /// </summary>
    private const string DefaultContainerName = "fakvio-files";

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
        string blobPath,
        byte[] content,
        string contentType,
        CancellationToken ct = default)
    {
        var (containerClient, blobClient) = await GetBlobAndContainerClientAsync(blobPath, ct);

        // Ensure the container exists before uploading.
        // CreateIfNotExistsAsync is idempotent — safe to call on every upload.
        await containerClient.CreateIfNotExistsAsync(cancellationToken: ct);

        // Set Content-Type so browsers handle the file correctly on download.
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
            blobPath, containerClient.Name, content.Length);

        return blobPath;
    }

    /// <inheritdoc />
    public async Task<byte[]> DownloadAsync(
        string blobPath,
        CancellationToken ct = default)
    {
        var (_, blobClient) = await GetBlobAndContainerClientAsync(blobPath, ct);

        var response = await blobClient.DownloadContentAsync(ct);
        return response.Value.Content.ToArray();
    }

    /// <inheritdoc />
    public async Task<bool> DeleteAsync(
        string blobPath,
        CancellationToken ct = default)
    {
        var (containerClient, blobClient) = await GetBlobAndContainerClientAsync(blobPath, ct);

        var response = await blobClient.DeleteIfExistsAsync(cancellationToken: ct);

        if (response.Value)
        {
            _logger.LogInformation("Deleted blob {BlobPath} from container {Container}", blobPath, containerClient.Name);
        }

        return response.Value;
    }

    /// <inheritdoc />
    public async Task<bool> ExistsAsync(
        string blobPath,
        CancellationToken ct = default)
    {
        var (_, blobClient) = await GetBlobAndContainerClientAsync(blobPath, ct);
        var response = await blobClient.ExistsAsync(ct);
        return response.Value;
    }

    // ─── Private Helpers ─────────────────────────────────────────────────────

    /// <summary>
    /// Creates a <see cref="BlobContainerClient"/> and <see cref="BlobClient"/> for the
    /// specified blob path. Resolves both connection string and container name using
    /// the same 3-tier fallback (company → system → appsettings → default).
    /// Returns both clients so the caller can create the container if needed.
    /// </summary>
    private async Task<(BlobContainerClient Container, BlobClient Blob)> GetBlobAndContainerClientAsync(
        string blobPath, CancellationToken ct)
    {
        var (connectionString, containerName) = await ResolveStorageConfigAsync(ct);

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
    /// Resolves both the connection string AND the container name in a single pass —
    /// they share the same 3-tier fallback logic and reading them together avoids
    /// fetching CompanySystemSettings twice from the master database.
    ///
    /// Resolution order (per field, independently):
    /// 1. CompanySystemSettings (per-company override)
    /// 2. SystemConfiguration (system-wide DB)
    /// 3. appsettings.json
    /// 4. Container name only — hard-coded default "fakvio-files"
    ///
    /// Connection strings from the database are decrypted via <see cref="ICredentialProtector"/>.
    /// </summary>
    private async Task<(string? ConnectionString, string ContainerName)> ResolveStorageConfigAsync(CancellationToken ct)
    {
        string? connectionString = null;
        string? containerName = null;

        // Tier 1: Company-specific overrides
        var companyId = _tenantResolver.GetCurrentCompanyId();
        if (companyId.HasValue)
        {
            var companySettings = await _masterContext.CompanySystemSettings
                .AsNoTracking()
                .FirstOrDefaultAsync(s => s.CompanyId == companyId.Value, ct);

            if (companySettings != null)
            {
                if (!string.IsNullOrWhiteSpace(companySettings.AzureBlobConnectionString))
                {
                    connectionString = _credentialProtector.Decrypt(companySettings.AzureBlobConnectionString);
                    _logger.LogDebug("Using company Azure Blob connection string for CompanyId {CompanyId}", companyId.Value);
                }

                if (!string.IsNullOrWhiteSpace(companySettings.AzureBlobContainerName))
                {
                    containerName = companySettings.AzureBlobContainerName;
                }
            }
        }

        // Tier 2: System-wide DB config (only fills fields still missing)
        if (connectionString == null || containerName == null)
        {
            var systemConfig = await _masterContext.Set<Domain.Entities.SystemConfiguration>()
                .AsNoTracking()
                .OrderBy(c => c.Id)
                .FirstOrDefaultAsync(ct);

            if (systemConfig != null)
            {
                if (connectionString == null && !string.IsNullOrWhiteSpace(systemConfig.AzureBlobConnectionString))
                {
                    connectionString = _credentialProtector.Decrypt(systemConfig.AzureBlobConnectionString);
                    _logger.LogDebug("Using system-wide Azure Blob connection string from SystemConfiguration");
                }

                if (containerName == null && !string.IsNullOrWhiteSpace(systemConfig.AzureBlobContainerName))
                {
                    containerName = systemConfig.AzureBlobContainerName;
                }
            }
        }

        // Tier 3: appsettings.json fallback
        if (connectionString == null)
        {
            var appSettingsCs = _configuration["AzureBlobStorage:ConnectionString"];
            if (!string.IsNullOrWhiteSpace(appSettingsCs))
            {
                connectionString = appSettingsCs;
                _logger.LogDebug("Using Azure Blob connection string from appsettings.json");
            }
        }

        if (containerName == null)
        {
            var appSettingsContainer = _configuration["AzureBlobStorage:ContainerName"];
            if (!string.IsNullOrWhiteSpace(appSettingsContainer))
            {
                containerName = appSettingsContainer;
            }
        }

        // Tier 4: hard-coded default for container name (connection string has no default)
        containerName ??= DefaultContainerName;

        return (connectionString, containerName);
    }
}

using InvoiceApi.Contracts.Dto.CloudStorage;
using InvoiceApi.Application.Service;
using InvoiceApi.Domain.Enums;
using InvoiceApi.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace InvoiceApi.Infrastructure.Service.CloudStorage;

/// <summary>
/// Orchestrates cloud storage operations across all enabled providers.
/// This is the single entry point that the rest of the application calls for cloud storage.
///
/// Routing: Each method that takes an ECloudStorageProvider parameter resolves
/// the correct IExternalCloudStorage implementation from the injected collection.
///
/// Upload fan-out: UploadInvoicePdfAsync iterates over ALL enabled providers
/// for the current company and uploads the PDF to each one. Individual provider
/// failures are logged as warnings but don't throw — the invoice workflow continues.
///
/// Dependencies:
/// - IEnumerable&lt;IExternalCloudStorage&gt; — all registered provider implementations
/// - MasterDbContext — reads CompanySystemSettings to check which providers are enabled
/// - ITenantResolver — determines the current company context
/// </summary>
public class CloudStorageOrchestrator : ICloudStorageOrchestrator
{
    private readonly IEnumerable<IExternalCloudStorage> _providers;
    private readonly MasterDbContext _masterContext;
    private readonly ITenantResolver _tenantResolver;
    private readonly ILogger<CloudStorageOrchestrator> _logger;

    public CloudStorageOrchestrator(
        IEnumerable<IExternalCloudStorage> providers,
        MasterDbContext masterContext,
        ITenantResolver tenantResolver,
        ILogger<CloudStorageOrchestrator> logger)
    {
        _providers = providers;
        _masterContext = masterContext;
        _tenantResolver = tenantResolver;
        _logger = logger;
    }

    /// <inheritdoc />
    public async Task<List<CloudStorageFileResult>> UploadInvoicePdfAsync(
        long invoiceId, byte[] pdfBytes, string fileName, CancellationToken ct = default)
    {
        var results = new List<CloudStorageFileResult>();

        // Get the current company's cloud storage settings to determine which providers are enabled
        var companyId = _tenantResolver.GetCurrentCompanyId();
        if (!companyId.HasValue)
        {
            _logger.LogWarning("No company context — skipping cloud storage upload for invoice {InvoiceId}", invoiceId);
            return results;
        }

        var settings = await _masterContext.CompanySystemSettings
            .AsNoTracking()
            .FirstOrDefaultAsync(s => s.CompanyId == companyId.Value, ct);

        if (settings == null)
        {
            _logger.LogWarning("CompanySystemSettings not found for company {CompanyId}", companyId);
            return results;
        }

        // Check each provider — upload to all enabled providers sequentially
        // (sequential to avoid DbContext thread safety issues)
        if (settings.GoogleDriveEnabled == true)
        {
            await UploadToProviderAsync(ECloudStorageProvider.GoogleDrive, invoiceId, pdfBytes, fileName, results, ct);
        }

        if (settings.OneDriveEnabled == true)
        {
            await UploadToProviderAsync(ECloudStorageProvider.OneDrive, invoiceId, pdfBytes, fileName, results, ct);
        }

        return results;
    }

    /// <inheritdoc />
    public async Task<List<CloudStorageStatusDto>> GetProvidersStatusAsync(CancellationToken ct = default)
    {
        var companyId = _tenantResolver.GetCurrentCompanyId()
            ?? throw new InvalidOperationException("No company context.");

        var settings = await _masterContext.CompanySystemSettings
            .AsNoTracking()
            .FirstOrDefaultAsync(s => s.CompanyId == companyId, ct);

        // Return status for both supported providers
        return new List<CloudStorageStatusDto>
        {
            new CloudStorageStatusDto
            {
                Provider = ECloudStorageProvider.GoogleDrive,
                IsConnected = settings?.GoogleDriveEnabled == true &&
                              !string.IsNullOrEmpty(settings.GoogleDriveRefreshToken),
                FolderId = settings?.GoogleDriveFolderId,
                FolderName = settings?.GoogleDriveFolderName,
                TokenExpiresAt = settings?.GoogleDriveTokenExpiresAt
            },
            new CloudStorageStatusDto
            {
                Provider = ECloudStorageProvider.OneDrive,
                IsConnected = settings?.OneDriveEnabled == true &&
                              !string.IsNullOrEmpty(settings.OneDriveRefreshToken),
                FolderId = settings?.OneDriveFolderId,
                FolderName = settings?.OneDriveFolderName,
                TokenExpiresAt = settings?.OneDriveTokenExpiresAt
            }
        };
    }

    /// <inheritdoc />
    public async Task<List<CloudStorageFolderDto>> ListFoldersAsync(
        ECloudStorageProvider provider, string? parentId, CancellationToken ct = default)
    {
        var impl = ResolveProvider(provider);
        return await impl.ListFoldersAsync(parentId, ct);
    }

    /// <inheritdoc />
    public async Task<string> GetAuthorizationUrlAsync(
        ECloudStorageProvider provider, string redirectUri, CancellationToken ct = default)
    {
        var impl = ResolveProvider(provider);
        return await impl.GetAuthorizationUrlAsync(redirectUri, ct);
    }

    /// <inheritdoc />
    public async Task<bool> ExchangeCodeAsync(
        ECloudStorageProvider provider, string code, string redirectUri, CancellationToken ct = default)
    {
        var impl = ResolveProvider(provider);
        return await impl.ExchangeCodeAsync(code, redirectUri, ct);
    }

    /// <inheritdoc />
    public async Task<bool> DisconnectAsync(ECloudStorageProvider provider, CancellationToken ct = default)
    {
        var impl = ResolveProvider(provider);
        return await impl.DisconnectAsync(ct);
    }

    /// <inheritdoc />
    public async Task<bool> SetFolderAsync(
        ECloudStorageProvider provider, CloudStorageSetFolderRequest request, CancellationToken ct = default)
    {
        var companyId = _tenantResolver.GetCurrentCompanyId()
            ?? throw new InvalidOperationException("No company context.");

        var settings = await _masterContext.CompanySystemSettings
            .FirstOrDefaultAsync(s => s.CompanyId == companyId, ct)
            ?? throw new InvalidOperationException($"CompanySystemSettings not found for company {companyId}.");

        // Update the folder setting for the specified provider
        switch (provider)
        {
            case ECloudStorageProvider.GoogleDrive:
                settings.GoogleDriveFolderId = request.FolderId;
                settings.GoogleDriveFolderName = request.FolderName;
                break;
            case ECloudStorageProvider.OneDrive:
                settings.OneDriveFolderId = request.FolderId;
                settings.OneDriveFolderName = request.FolderName;
                break;
            default:
                throw new ArgumentException($"Unsupported provider: {provider}");
        }

        await _masterContext.SaveChangesAsync(ct);

        _logger.LogInformation("Set {Provider} folder to '{FolderName}' (ID: {FolderId}) for company {CompanyId}",
            provider, request.FolderName, request.FolderId, companyId);
        return true;
    }

    /// <inheritdoc />
    public async Task<bool> TestConnectionAsync(ECloudStorageProvider provider, CancellationToken ct = default)
    {
        var impl = ResolveProvider(provider);
        return await impl.TestConnectionAsync(ct);
    }

    // ─── Private Helpers ─────────────────────────────────────────────────────

    /// <summary>
    /// Resolves the IExternalCloudStorage implementation for the specified provider.
    /// Throws if no implementation is registered for the given provider.
    /// </summary>
    private IExternalCloudStorage ResolveProvider(ECloudStorageProvider provider)
    {
        var impl = _providers.FirstOrDefault(p => p.Provider == provider)
            ?? throw new ArgumentException($"No cloud storage implementation registered for provider: {provider}");
        return impl;
    }

    /// <summary>
    /// Uploads a PDF to a single provider with error handling.
    /// Failures are logged as warnings and added to the results list as null
    /// (the calling code only includes successful results).
    /// </summary>
    private async Task UploadToProviderAsync(
        ECloudStorageProvider provider, long invoiceId,
        byte[] pdfBytes, string fileName,
        List<CloudStorageFileResult> results,
        CancellationToken ct)
    {
        try
        {
            var impl = ResolveProvider(provider);
            var result = await impl.UploadFileAsync(fileName, pdfBytes, "application/pdf", ct);
            results.Add(result);
            _logger.LogInformation(
                "Invoice {InvoiceId} PDF uploaded to {Provider}: {FileId}",
                invoiceId, provider, result.FileId);
        }
        catch (Exception ex)
        {
            // Log but don't throw — one provider failing should not block others
            _logger.LogWarning(ex,
                "Failed to upload invoice {InvoiceId} PDF to {Provider}",
                invoiceId, provider);
        }
    }
}

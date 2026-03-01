using Fakvio.Contracts.Dto.CloudStorage;
using Fakvio.Domain.Enums;

namespace Fakvio.Application.Service;

/// <summary>
/// Orchestrates cloud storage operations across all enabled providers.
/// This is the high-level service that the rest of the application calls —
/// it routes requests to the correct IExternalCloudStorage implementation
/// based on the provider parameter.
///
/// Key responsibilities:
/// - Upload invoice PDFs to ALL enabled providers (fire-and-forget, non-blocking)
/// - Aggregate provider statuses for the settings UI
/// - Delegate single-provider operations (auth, folders, disconnect) to the correct implementation
///
/// Inject this instead of IExternalCloudStorage directly — it handles provider resolution
/// and multi-provider fan-out automatically.
/// </summary>
public interface ICloudStorageOrchestrator
{
    /// <summary>
    /// Uploads an invoice PDF to ALL enabled cloud storage providers for the current company.
    /// Called after PDF generation (e.g., when completing an invoice or explicitly exporting).
    /// Failures on individual providers are logged as warnings but do NOT throw —
    /// a failed upload to one provider should not block the invoice workflow.
    /// </summary>
    /// <param name="invoiceId">The ID of the invoice being uploaded (for logging/naming).</param>
    /// <param name="pdfBytes">The PDF file content as a byte array.</param>
    /// <param name="fileName">The file name for the uploaded PDF (e.g., "INV-2026001.pdf").</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>
    /// List of results from each provider that successfully uploaded the file.
    /// Providers that failed are excluded from the list (failures are logged).
    /// </returns>
    Task<List<CloudStorageFileResult>> UploadInvoicePdfAsync(
        long invoiceId,
        byte[] pdfBytes,
        string fileName,
        CancellationToken ct = default);

    /// <summary>
    /// Gets the connection status of all supported cloud storage providers for the current company.
    /// Returns one CloudStorageStatusDto per supported provider (GoogleDrive, OneDrive),
    /// showing whether each is connected, which folder is selected, and when the token expires.
    /// </summary>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>List of status DTOs, one per supported provider.</returns>
    Task<List<CloudStorageStatusDto>> GetProvidersStatusAsync(CancellationToken ct = default);

    /// <summary>
    /// Lists subfolders in the specified parent folder for a specific provider.
    /// Delegates to the corresponding IExternalCloudStorage.ListFoldersAsync.
    /// Used by the folder picker UI.
    /// </summary>
    /// <param name="provider">Which cloud storage provider to list folders from.</param>
    /// <param name="parentId">Parent folder ID, or null for root-level folders.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>List of subfolders.</returns>
    Task<List<CloudStorageFolderDto>> ListFoldersAsync(
        ECloudStorageProvider provider,
        string? parentId,
        CancellationToken ct = default);

    /// <summary>
    /// Generates the OAuth authorization URL for a specific provider.
    /// Delegates to the corresponding IExternalCloudStorage.GetAuthorizationUrlAsync.
    /// </summary>
    /// <param name="provider">Which provider to generate the auth URL for.</param>
    /// <param name="redirectUri">The callback URI for the OAuth flow.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The full OAuth authorization URL.</returns>
    Task<string> GetAuthorizationUrlAsync(
        ECloudStorageProvider provider,
        string redirectUri,
        CancellationToken ct = default);

    /// <summary>
    /// Exchanges an OAuth authorization code for tokens for a specific provider.
    /// Delegates to the corresponding IExternalCloudStorage.ExchangeCodeAsync.
    /// </summary>
    /// <param name="provider">Which provider to exchange the code for.</param>
    /// <param name="code">The authorization code from the OAuth callback.</param>
    /// <param name="redirectUri">The same redirect URI used in GetAuthorizationUrlAsync.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>True if tokens were obtained and stored successfully.</returns>
    Task<bool> ExchangeCodeAsync(
        ECloudStorageProvider provider,
        string code,
        string redirectUri,
        CancellationToken ct = default);

    /// <summary>
    /// Disconnects a specific cloud storage provider by revoking tokens and clearing credentials.
    /// Delegates to the corresponding IExternalCloudStorage.DisconnectAsync.
    /// </summary>
    /// <param name="provider">Which provider to disconnect.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>True if the provider was disconnected successfully.</returns>
    Task<bool> DisconnectAsync(
        ECloudStorageProvider provider,
        CancellationToken ct = default);

    /// <summary>
    /// Sets the destination folder for a specific provider.
    /// Updates CompanySystemSettings with the selected folder ID and name.
    /// </summary>
    /// <param name="provider">Which provider to set the folder for.</param>
    /// <param name="request">Folder ID and name to set.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>True if the folder was set successfully.</returns>
    Task<bool> SetFolderAsync(
        ECloudStorageProvider provider,
        CloudStorageSetFolderRequest request,
        CancellationToken ct = default);

    /// <summary>
    /// Tests the connection for a specific provider by making a lightweight API call.
    /// Delegates to the corresponding IExternalCloudStorage.TestConnectionAsync.
    /// </summary>
    /// <param name="provider">Which provider to test.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>True if the connection is healthy.</returns>
    Task<bool> TestConnectionAsync(
        ECloudStorageProvider provider,
        CancellationToken ct = default);
}

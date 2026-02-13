using InvoiceApi.Application.Dto.CloudStorage;
using InvoiceApi.Domain.Enums;

namespace InvoiceApi.Application.Service;

/// <summary>
/// Abstraction for an external cloud storage provider (Google Drive, OneDrive, etc.).
/// Each implementation handles OAuth 2.0 token management and file operations
/// for one specific provider.
///
/// Lifecycle of a cloud storage connection:
/// 1. GetAuthorizationUrlAsync → returns OAuth URL → user authorizes in browser
/// 2. ExchangeCodeAsync → trades authorization code for access + refresh tokens
/// 3. UploadFileAsync → uploads PDF bytes to the provider
/// 4. RefreshTokenAsync → renews expired access tokens automatically
/// 5. DisconnectAsync → revokes tokens and clears stored credentials
///
/// Each implementation reads/writes OAuth tokens to CompanySystemSettings in the master database.
/// The ITenantResolver provides the current CompanyId to locate the correct settings row.
/// </summary>
public interface IExternalCloudStorage
{
    /// <summary>
    /// Identifies which cloud storage provider this implementation handles.
    /// Used by CloudStorageOrchestrator to route requests to the correct implementation.
    /// </summary>
    ECloudStorageProvider Provider { get; }

    /// <summary>
    /// Generates the OAuth 2.0 authorization URL that the user must visit to grant access.
    /// The URL includes the client ID, requested scopes, redirect URI, and a state parameter.
    /// After the user approves, the provider redirects back to the callback URL with an authorization code.
    /// </summary>
    /// <param name="redirectUri">
    /// The URL the OAuth provider will redirect to after the user approves/denies.
    /// Must match one of the redirect URIs configured in the provider's developer console.
    /// </param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The full OAuth authorization URL to redirect the user to.</returns>
    Task<string> GetAuthorizationUrlAsync(string redirectUri, CancellationToken ct = default);

    /// <summary>
    /// Exchanges an OAuth 2.0 authorization code for access and refresh tokens.
    /// Called after the user approves access and the provider redirects back with a code.
    /// The obtained tokens are stored in CompanySystemSettings for the current company.
    /// </summary>
    /// <param name="code">The authorization code received from the OAuth callback.</param>
    /// <param name="redirectUri">
    /// The same redirect URI used in GetAuthorizationUrlAsync (must match exactly).
    /// </param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>True if the code was exchanged successfully and tokens were stored.</returns>
    Task<bool> ExchangeCodeAsync(string code, string redirectUri, CancellationToken ct = default);

    /// <summary>
    /// Refreshes an expired access token using the stored refresh token.
    /// Called automatically before API calls when the access token has expired.
    /// Updates CompanySystemSettings with the new access token and expiry timestamp.
    /// </summary>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>True if the token was refreshed successfully; false if the refresh token is invalid/revoked.</returns>
    Task<bool> RefreshTokenAsync(CancellationToken ct = default);

    /// <summary>
    /// Uploads a file (typically an invoice PDF) to the cloud storage provider.
    /// If a destination folder is configured in CompanySystemSettings, the file is placed there.
    /// Otherwise, the file is uploaded to the root of the user's storage.
    /// Automatically refreshes the access token if it has expired.
    /// </summary>
    /// <param name="fileName">
    /// Name for the uploaded file (e.g., "INV-2026001.pdf").
    /// If a file with the same name exists in the target folder, behavior depends on the provider
    /// (Google Drive allows duplicates; OneDrive may rename or replace).
    /// </param>
    /// <param name="content">The file content as a byte array (PDF bytes).</param>
    /// <param name="contentType">MIME type of the file (e.g., "application/pdf").</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>Result containing the uploaded file's ID and web URL.</returns>
    Task<CloudStorageFileResult> UploadFileAsync(
        string fileName,
        byte[] content,
        string contentType,
        CancellationToken ct = default);

    /// <summary>
    /// Lists subfolders in the specified parent folder.
    /// Used by the folder picker UI to let the user browse and select a destination folder.
    /// Pass null for parentId to list root-level folders.
    /// </summary>
    /// <param name="parentId">
    /// The ID of the parent folder to list children of.
    /// Null = list root-level folders.
    /// </param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>List of subfolders in the specified parent.</returns>
    Task<List<CloudStorageFolderDto>> ListFoldersAsync(string? parentId, CancellationToken ct = default);

    /// <summary>
    /// Tests the connection to the cloud storage provider by verifying that the stored tokens are valid.
    /// Attempts a lightweight API call (e.g., list root folder) to confirm the connection works.
    /// Automatically refreshes the access token if expired.
    /// </summary>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>True if the connection is healthy; false if tokens are invalid or the API is unreachable.</returns>
    Task<bool> TestConnectionAsync(CancellationToken ct = default);

    /// <summary>
    /// Disconnects the cloud storage provider by revoking OAuth tokens and clearing stored credentials.
    /// After disconnection, the provider is no longer enabled and no uploads will be attempted.
    /// The user must re-authorize to reconnect.
    /// </summary>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>True if disconnection was successful.</returns>
    Task<bool> DisconnectAsync(CancellationToken ct = default);
}

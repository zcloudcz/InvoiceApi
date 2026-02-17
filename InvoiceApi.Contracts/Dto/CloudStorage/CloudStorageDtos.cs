using InvoiceApi.Domain.Enums;

namespace InvoiceApi.Contracts.Dto.CloudStorage;

/// <summary>
/// Represents a folder in a cloud storage provider (Google Drive or OneDrive).
/// Used when the user browses folders to select a destination for invoice PDF uploads.
/// The folder tree is loaded lazily — parentId determines which level to list.
/// </summary>
public class CloudStorageFolderDto
{
    /// <summary>
    /// Unique identifier of the folder in the cloud provider's system.
    /// For Google Drive: the folder's Google Drive ID (e.g., "1ABC...xyz").
    /// For OneDrive: the driveItem ID (e.g., "01ABCDEF...").
    /// </summary>
    public string Id { get; set; } = string.Empty;

    /// <summary>
    /// Human-readable name of the folder (e.g., "Invoices 2026").
    /// Displayed in the folder picker UI.
    /// </summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>
    /// ID of the parent folder, or null if this is a root-level folder.
    /// Used to build the folder hierarchy in the browser UI.
    /// </summary>
    public string? ParentId { get; set; }
}

/// <summary>
/// Result returned after successfully uploading a file to a cloud storage provider.
/// Contains the provider-specific file ID and a web URL for viewing/downloading the file.
/// </summary>
public class CloudStorageFileResult
{
    /// <summary>
    /// Name of the uploaded file (e.g., "INV-2026001.pdf").
    /// Matches the fileName parameter passed to UploadFileAsync.
    /// </summary>
    public string FileName { get; set; } = string.Empty;

    /// <summary>
    /// Provider-specific unique identifier of the uploaded file.
    /// Can be used later to reference the file (e.g., for deletion or download).
    /// </summary>
    public string FileId { get; set; } = string.Empty;

    /// <summary>
    /// Web URL where the uploaded file can be viewed or downloaded.
    /// For Google Drive: a shareable link or web view URL.
    /// For OneDrive: a direct web URL to the file.
    /// </summary>
    public string? Url { get; set; }
}

/// <summary>
/// Provides the current connection status of a cloud storage provider for a company.
/// Used by the settings UI to show which providers are connected and their configuration.
/// </summary>
public class CloudStorageStatusDto
{
    /// <summary>
    /// Which cloud storage provider this status represents.
    /// </summary>
    public ECloudStorageProvider Provider { get; set; }

    /// <summary>
    /// Whether the provider is currently connected (has valid tokens and is enabled).
    /// True = connected and ready to upload; False = not configured or tokens expired/revoked.
    /// </summary>
    public bool IsConnected { get; set; }

    /// <summary>
    /// The ID of the selected destination folder, or null if uploading to root.
    /// </summary>
    public string? FolderId { get; set; }

    /// <summary>
    /// Display name of the selected destination folder, or null if uploading to root.
    /// </summary>
    public string? FolderName { get; set; }

    /// <summary>
    /// When the current access token expires (UTC).
    /// Null if no token is stored (provider not connected).
    /// The system automatically refreshes tokens before expiry.
    /// </summary>
    public DateTime? TokenExpiresAt { get; set; }
}

/// <summary>
/// Request body for setting the destination folder where invoice PDFs are uploaded.
/// Sent from the folder picker UI after the user selects a folder.
/// </summary>
public class CloudStorageSetFolderRequest
{
    /// <summary>
    /// Provider-specific folder ID to set as the upload destination.
    /// Null clears the folder selection (uploads go to root).
    /// </summary>
    public string? FolderId { get; set; }

    /// <summary>
    /// Human-readable folder name for display in the UI.
    /// Stored alongside FolderId for convenience — avoids an extra API call to resolve the name.
    /// </summary>
    public string? FolderName { get; set; }
}

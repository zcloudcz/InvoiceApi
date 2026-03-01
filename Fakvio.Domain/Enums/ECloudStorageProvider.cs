namespace Fakvio.Domain.Enums;

/// <summary>
/// Supported cloud storage providers for automatic invoice PDF backup.
/// Each company (tenant) can enable one or more providers in CompanySystemSettings.
/// When enabled, the CloudStorageOrchestrator automatically uploads generated PDFs
/// to all active providers after invoice completion.
///
/// Value assignments:
///   0 = None (no cloud storage configured — default)
///   1 = Google Drive (uses Google OAuth 2.0 with drive.file scope)
///   2 = OneDrive (uses Microsoft Graph API with Files.ReadWrite scope)
/// </summary>
public enum ECloudStorageProvider
{
    /// <summary>
    /// No cloud storage provider configured.
    /// This is the default value — the system does not upload PDFs anywhere.
    /// </summary>
    None = 0,

    /// <summary>
    /// Google Drive cloud storage.
    /// Uses OAuth 2.0 authorization code flow with "drive.file" scope
    /// (only accesses files created by the app, not the entire Drive).
    /// Tokens are stored in CompanySystemSettings (GoogleDriveAccessToken, GoogleDriveRefreshToken).
    /// </summary>
    GoogleDrive = 1,

    /// <summary>
    /// Microsoft OneDrive cloud storage.
    /// Uses OAuth 2.0 authorization code flow with "Files.ReadWrite" scope
    /// (read/write access to the user's OneDrive files).
    /// Tokens are stored in CompanySystemSettings (OneDriveAccessToken, OneDriveRefreshToken).
    /// </summary>
    OneDrive = 2
}

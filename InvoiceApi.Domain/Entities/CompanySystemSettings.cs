using InvoiceApi.Domain.Common;

namespace InvoiceApi.Domain.Entities;

/// <summary>
/// Stores multi-tenant infrastructure configuration for each company (tenant).
/// Each company that uses the system gets its own row here with database name,
/// connection details, and provisioning status.
///
/// This entity lives in the MASTER database — it's used to resolve which
/// tenant database a request should be routed to based on the JWT CompanyId claim.
///
/// Lifecycle: Company created → CompanySystemSettings added → Provision triggered →
/// Database created → Migrations applied → Code tables copied → IsProvisioned = true.
/// </summary>
public class CompanySystemSettings : BaseEntity
{
    /// <summary>
    /// Foreign key to the Client entity (where IsIssuer = true).
    /// Each company (issuer) gets exactly one CompanySystemSettings record.
    /// </summary>
    public long CompanyId { get; set; }

    /// <summary>
    /// Navigation property to the Client (company) this settings record belongs to.
    /// The Client.IsIssuer should always be true for the linked record.
    /// </summary>
    public Client Company { get; set; } = null!;

    /// <summary>
    /// PostgreSQL database name for this tenant.
    /// Convention: "invoiceapi_tenant_{companyId}" (e.g., "invoiceapi_tenant_42").
    /// Set during provisioning — immutable after that.
    /// </summary>
    public string DatabaseName { get; set; } = string.Empty;

    /// <summary>
    /// Optional full connection string override for this tenant.
    /// If null, the system builds the connection string from the master template
    /// by replacing the database name. Use this only for tenants on different servers.
    /// </summary>
    public string? ConnectionString { get; set; }

    /// <summary>
    /// When was the database provisioned (created + migrated + seeded)?
    /// Null means the database hasn't been provisioned yet.
    /// </summary>
    public DateTime? ProvisionedAt { get; set; }

    /// <summary>
    /// Has the tenant database been provisioned?
    /// true = database exists and has been migrated + seeded.
    /// false = company registered but database not yet created.
    /// </summary>
    public bool IsProvisioned { get; set; }

    /// <summary>
    /// Is this tenant currently active?
    /// Inactive tenants cannot log in — their requests are rejected by middleware.
    /// Used for billing suspension, account deactivation, etc.
    /// </summary>
    public bool IsActive { get; set; } = true;

    /// <summary>
    /// Maximum number of users allowed for this tenant.
    /// Null means no limit (unlimited users).
    /// Used for billing tier enforcement.
    /// </summary>
    public int? MaxUsers { get; set; }

    /// <summary>
    /// Free-text admin notes about this tenant.
    /// Used by SysAdmin for internal tracking (e.g., "Premium tier", "Trial until 2026-06").
    /// Not visible to tenant users.
    /// </summary>
    public string? AdminNotes { get; set; }

    // ─── Company-Specific SMTP Settings ───────────────────────────────────────
    // When configured (SmtpHost is non-empty), emails sent on behalf of this company
    // use these settings instead of the system-wide SMTP from SystemConfiguration.
    // All properties are nullable — null means "use system SMTP" (fallback).

    /// <summary>
    /// SMTP server hostname (e.g., "smtp.gmail.com").
    /// When non-empty, enables company-specific SMTP for outgoing emails.
    /// </summary>
    public string? SmtpHost { get; set; }

    /// <summary>
    /// SMTP server port number (e.g., 587 for TLS, 465 for SSL).
    /// Defaults to 587 if not specified when SmtpHost is set.
    /// </summary>
    public int? SmtpPort { get; set; }

    /// <summary>
    /// SMTP authentication username (often the sender email address).
    /// </summary>
    public string? SmtpUsername { get; set; }

    /// <summary>
    /// SMTP authentication password.
    /// Stored encrypted in production — never exposed in read DTOs.
    /// </summary>
    public string? SmtpPassword { get; set; }

    /// <summary>
    /// "From" email address displayed to recipients (e.g., "invoices@mycompany.com").
    /// Falls back to SmtpUsername if not set.
    /// </summary>
    public string? SmtpSenderEmail { get; set; }

    /// <summary>
    /// "From" display name shown to recipients (e.g., "My Company Invoices").
    /// Falls back to "Invoice" if not set.
    /// </summary>
    public string? SmtpSenderName { get; set; }

    /// <summary>
    /// Whether to use SSL/TLS when connecting to the SMTP server.
    /// Defaults to true if not specified when SmtpHost is set.
    /// </summary>
    public bool? SmtpUseSsl { get; set; }

    // ─── Google Drive Cloud Storage Settings ─────────────────────────────────
    // When GoogleDriveEnabled is true, the CloudStorageOrchestrator uploads
    // generated invoice PDFs to the user's Google Drive folder automatically.
    // OAuth tokens are obtained via the authorization code flow and stored here.
    // The access token is short-lived (~1 hour); the refresh token is used to renew it.

    /// <summary>
    /// Whether Google Drive integration is enabled for this company.
    /// When true, the system uploads invoice PDFs to Google Drive after generation.
    /// </summary>
    public bool? GoogleDriveEnabled { get; set; }

    /// <summary>
    /// Google OAuth 2.0 access token for Google Drive API calls.
    /// Short-lived (~1 hour) — automatically refreshed using GoogleDriveRefreshToken.
    /// Stored encrypted in production.
    /// </summary>
    public string? GoogleDriveAccessToken { get; set; }

    /// <summary>
    /// Google OAuth 2.0 refresh token for obtaining new access tokens.
    /// Long-lived — only invalidated when user revokes access.
    /// Stored encrypted in production.
    /// </summary>
    public string? GoogleDriveRefreshToken { get; set; }

    /// <summary>
    /// Expiration timestamp of the current Google Drive access token.
    /// When DateTime.UtcNow >= this value, the token must be refreshed.
    /// </summary>
    public DateTime? GoogleDriveTokenExpiresAt { get; set; }

    /// <summary>
    /// Google Drive folder ID where invoice PDFs are uploaded.
    /// If null, files are uploaded to the root of the user's Drive.
    /// Set via the "Set Folder" UI in cloud storage settings.
    /// </summary>
    public string? GoogleDriveFolderId { get; set; }

    /// <summary>
    /// Display name of the selected Google Drive folder.
    /// Stored for UI display purposes — the actual upload uses GoogleDriveFolderId.
    /// </summary>
    public string? GoogleDriveFolderName { get; set; }

    // ─── OneDrive Cloud Storage Settings ─────────────────────────────────────
    // When OneDriveEnabled is true, the CloudStorageOrchestrator uploads
    // generated invoice PDFs to the user's OneDrive folder automatically.
    // Uses Microsoft Graph API with Files.ReadWrite scope.

    /// <summary>
    /// Whether OneDrive integration is enabled for this company.
    /// When true, the system uploads invoice PDFs to OneDrive after generation.
    /// </summary>
    public bool? OneDriveEnabled { get; set; }

    /// <summary>
    /// Microsoft OAuth 2.0 access token for OneDrive/Graph API calls.
    /// Short-lived (~1 hour) — automatically refreshed using OneDriveRefreshToken.
    /// Stored encrypted in production.
    /// </summary>
    public string? OneDriveAccessToken { get; set; }

    /// <summary>
    /// Microsoft OAuth 2.0 refresh token for obtaining new access tokens.
    /// Long-lived — only invalidated when user revokes access or token expires.
    /// Stored encrypted in production.
    /// </summary>
    public string? OneDriveRefreshToken { get; set; }

    /// <summary>
    /// Expiration timestamp of the current OneDrive access token.
    /// When DateTime.UtcNow >= this value, the token must be refreshed.
    /// </summary>
    public DateTime? OneDriveTokenExpiresAt { get; set; }

    /// <summary>
    /// OneDrive folder ID (driveItem ID) where invoice PDFs are uploaded.
    /// If null, files are uploaded to the root of the user's OneDrive.
    /// Set via the "Set Folder" UI in cloud storage settings.
    /// </summary>
    public string? OneDriveFolderId { get; set; }

    /// <summary>
    /// Display name of the selected OneDrive folder.
    /// Stored for UI display purposes — the actual upload uses OneDriveFolderId.
    /// </summary>
    public string? OneDriveFolderName { get; set; }
}

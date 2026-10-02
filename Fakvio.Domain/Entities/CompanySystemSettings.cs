using Fakvio.Domain.Common;
using Fakvio.Domain.Enums;

namespace Fakvio.Domain.Entities;

/// <summary>
/// Stores multi-tenant infrastructure configuration for each company (tenant).
/// Each company that uses the system gets its own row here with schema name
/// and provisioning status.
///
/// This entity lives in the MASTER database (public schema) — it's used to resolve
/// which tenant schema a request should be routed to based on the JWT CompanyId claim.
///
/// Architecture: Single PostgreSQL database with schema-per-tenant isolation.
/// Master data lives in the "public" schema, each tenant gets "tenant_{companyId}" schema.
///
/// Lifecycle: Company created → CompanySystemSettings added → Provision triggered →
/// Schema created → Migrations applied → Code tables copied → IsProvisioned = true.
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
    /// PostgreSQL schema name for this tenant within the shared database.
    /// Convention: "tenant_{companyId}" (e.g., "tenant_42").
    /// Set during provisioning — immutable after that.
    /// The schema isolates all tenant data (invoices, clients, etc.) from other tenants.
    /// </summary>
    public string SchemaName { get; set; } = string.Empty;

    /// <summary>
    /// When was the tenant schema provisioned (created + migrated + seeded)?
    /// Null means the schema hasn't been provisioned yet.
    /// </summary>
    public DateTime? ProvisionedAt { get; set; }

    /// <summary>
    /// Has the tenant schema been provisioned?
    /// true = schema exists and has been migrated + seeded.
    /// false = company registered but schema not yet created.
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

    // ─── Company-Specific AI Assistant Settings ──────────────────────────────
    // When configured (e.g., AiClaudeApiKey is non-empty), the AI chat assistant
    // uses these settings instead of the system-wide AI config from appsettings.json.
    // All properties are nullable — null/empty means "use system AI settings" (fallback).
    // This follows the same 3-tier pattern as SMTP:
    //   1. Company AI settings (this entity) → 2. System appsettings.json → 3. No provider

    /// <summary>
    /// Default AI provider for this company (e.g., "Claude", "OpenAI", "Gemini", "Ollama").
    /// When non-empty, overrides the system-wide DefaultProvider from appsettings.json.
    /// Null/empty means "use system default provider".
    /// </summary>
    public string? AiDefaultProvider { get; set; }

    /// <summary>
    /// Anthropic Claude API key for this company.
    /// When non-empty, the company uses its own Claude API key instead of the system-wide one.
    /// Stored encrypted in production — never exposed in read DTOs.
    /// </summary>
    public string? AiClaudeApiKey { get; set; }

    /// <summary>
    /// Claude model identifier (e.g., "claude-sonnet-4-6", "claude-opus-4-6").
    /// Null/empty means "use system default model".
    /// </summary>
    public string? AiClaudeModel { get; set; }

    /// <summary>
    /// OpenAI API key for this company.
    /// When non-empty, the company uses its own OpenAI key instead of the system-wide one.
    /// Stored encrypted in production — never exposed in read DTOs.
    /// </summary>
    public string? AiOpenAiApiKey { get; set; }

    /// <summary>
    /// OpenAI model identifier (e.g., "gpt-4o", "gpt-4-turbo").
    /// Null/empty means "use system default model".
    /// </summary>
    public string? AiOpenAiModel { get; set; }

    /// <summary>
    /// Google Gemini API key for this company.
    /// When non-empty, the company uses its own Gemini key instead of the system-wide one.
    /// Stored encrypted in production — never exposed in read DTOs.
    /// </summary>
    public string? AiGeminiApiKey { get; set; }

    /// <summary>
    /// Gemini model identifier (e.g., "gemini-2.0-flash").
    /// Null/empty means "use system default model".
    /// </summary>
    public string? AiGeminiModel { get; set; }

    /// <summary>
    /// Ollama server base URL for this company (e.g., "http://localhost:11434").
    /// When non-empty, the company uses its own Ollama server.
    /// </summary>
    public string? AiOllamaBaseUrl { get; set; }

    /// <summary>
    /// Ollama model identifier (e.g., "gemma3:12b", "llama3.2").
    /// Null/empty means "use system default model".
    /// </summary>
    public string? AiOllamaModel { get; set; }

    // ─── Azure Blob Storage Settings ────────────────────────────────────────
    // Company-specific blob storage configuration for file attachments.
    // When set, overrides the system-wide settings from SystemConfiguration.
    // Null/empty means "use system blob storage settings" (3-tier fallback).
    // Connection string is encrypted at rest via ICredentialProtector.

    /// <summary>
    /// Azure Blob Storage connection string for this company (encrypted at rest).
    /// When non-empty, this company uses its own storage account.
    /// Null/empty means "use system-wide AzureBlobConnectionString from SystemConfiguration".
    /// </summary>
    public string? AzureBlobConnectionString { get; set; }

    /// <summary>
    /// Legacy override for the blob container name prefix. Unused by current code —
    /// the new design uses one shared container (see <see cref="AzureBlobContainerName"/>).
    /// Kept for backwards compatibility; will be removed in a future cleanup.
    /// </summary>
    public string? AzureBlobContainerPrefix { get; set; }

    /// <summary>
    /// Per-company override for the shared blob container name.
    /// Null/empty means "use system-wide AzureBlobContainerName from SystemConfiguration"
    /// (which itself defaults to "fakvio-files" when also unset).
    ///
    /// This is rarely needed — only when a single tenant must store its files in
    /// a different container (e.g., for data residency). Combined with
    /// <see cref="AzureBlobConnectionString"/> override it allows a tenant to use
    /// an entirely separate storage account.
    /// </summary>
    public string? AzureBlobContainerName { get; set; }

    // ─── EPO (Electronic Tax Filing) Header Settings ──────────────────────
    // Fields required by the Czech Financial Administration EPO portal when generating
    // DPHDP3 (VAT return) and DPHKH1 (VAT control statement) XML submissions.
    // All fields are nullable — null means "not yet configured".
    // Missing required fields cause the EPO export to fail with EPO_HEADER_INCOMPLETE.

    /// <summary>
    /// Czech Financial Administration tax office code (c_ufo — kód finančního úřadu).
    /// Required for EPO DPHDP3 / DPHKH1 submissions.
    /// Valid range: 1–999 (3-digit code per MFČR číselník UFO).
    /// Example: 451 = Finanční úřad pro hl. m. Prahu.
    /// Null = not yet configured; EPO export will fail until this is set.
    /// </summary>
    public int? EpoTaxOfficeCode { get; set; }

    /// <summary>
    /// Czech Financial Administration territorial branch code (c_pracufo — kód územního pracoviště).
    /// Required for EPO DPHDP3 / DPHKH1 submissions.
    /// Example: 2017 = Finanční úřad pro hl. m. Prahu — Územní pracoviště Praha 1.
    /// Null = not yet configured.
    /// </summary>
    public int? EpoTaxOfficeBranchCode { get; set; }

    /// <summary>
    /// Phone number of the person filling in the EPO form.
    /// Stored for inclusion in EPO XML header metadata.
    /// Example: "+420 123 456 789".
    /// Null = not yet configured.
    /// </summary>
    public string? EpoContactPhone { get; set; }

    /// <summary>
    /// Email address of the person filling in the EPO form.
    /// Stored for inclusion in EPO XML header metadata.
    /// Null = not yet configured.
    /// </summary>
    public string? EpoContactEmail { get; set; }

    /// <summary>
    /// Full name of the authorized person for EPO document signing.
    /// Included in the EPO XML header as the authorized signatory.
    /// Example: "Ing. Jan Novák".
    /// Null = not yet configured.
    /// </summary>
    public string? EpoAuthorizedPersonName { get; set; }

    /// <summary>
    /// Default VAT reporting period type for this company (Monthly or Quarterly).
    /// Pre-fills the period type selector in the UI when generating EPO exports.
    /// Null = not yet configured; UI will require the user to select each time.
    /// </summary>
    public EVatPeriodType? EpoDefaultPeriodType { get; set; }

    // ─── EU OSS (One-Stop-Shop) Registration ───────────────────────────────
    // Whether this company (issuer) is registered for the EU OSS special scheme
    // ("zvláštní režim jednoho správního místa — režim Unie"). When true, B2C
    // sales to consumers in other EU member states are detected as OSS invoices
    // (see Fakvio.Infrastructure.Service.Oss.OssDetector) and must charge the
    // destination country's VAT rate instead of CZ VAT — see DEVGUIDE §4.16.

    /// <summary>
    /// Is this company registered for the EU OSS scheme? Only meaningful for VAT
    /// payers (<c>Client.IsVatPayer == true</c> on the issuer) — the UI only offers
    /// the toggle to VAT payers, but the flag itself does not enforce that here;
    /// <see cref="Fakvio.Infrastructure.Service.Oss.OssDetector"/> checks IsVatPayer too.
    /// </summary>
    public bool OssRegistered { get; set; }

    /// <summary>
    /// Date from which the OSS registration is effective. Null while not registered.
    /// Not currently used to gate detection by date (OSS registration in practice starts
    /// at a calendar-quarter boundary already) — kept for display/audit purposes.
    /// </summary>
    public DateTime? OssRegisteredSince { get; set; }
}

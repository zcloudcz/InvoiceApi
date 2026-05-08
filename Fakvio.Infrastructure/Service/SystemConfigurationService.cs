using Azure.Storage.Blobs;
using Fakvio.Application.Service;
using Fakvio.Contracts.Dto.SystemConfiguration;
using Fakvio.Domain.Entities;
using Fakvio.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace Fakvio.Infrastructure.Service;

/// <summary>
/// Service for managing the single-row SystemConfiguration table in the master database.
///
/// Design: The configuration row is created lazily on first read (GetOrCreateAsync).
/// This means fresh installs don't need a migration seed — the row is auto-created
/// with sensible defaults on first access.
///
/// Thread safety: Uses FirstOrDefaultAsync, so concurrent reads are safe.
/// Concurrent writes to the same row are handled by EF Core's optimistic concurrency
/// (last writer wins, which is acceptable for an admin-only settings page).
/// </summary>
public class SystemConfigurationService : ISystemConfigurationService
{
    private readonly MasterDbContext _context;
    private readonly ICredentialProtector _credentialProtector;
    private readonly IConfiguration _configuration;
    private readonly ILogger<SystemConfigurationService> _logger;

    public SystemConfigurationService(
        MasterDbContext context,
        ICredentialProtector credentialProtector,
        IConfiguration configuration,
        ILogger<SystemConfigurationService> logger)
    {
        _context = context;
        _credentialProtector = credentialProtector;
        _configuration = configuration;
        _logger = logger;
    }

    /// <inheritdoc />
    public async Task<SystemConfigurationDto> GetAsync(CancellationToken ct = default)
    {
        var entity = await GetOrCreateAsync(ct);
        return MapToDto(entity);
    }

    /// <inheritdoc />
    public async Task<SystemConfigurationDto> UpdateAsync(UpdateSystemConfigurationDto dto, CancellationToken ct = default)
    {
        var entity = await GetOrCreateAsync(ct);

        // ── Application Settings ──────────────────────────────────────────────
        entity.AppName = dto.AppName;
        entity.BlazorBaseUrl = dto.BlazorBaseUrl?.TrimEnd('/') ?? "";

        // ── SMTP Settings ─────────────────────────────────────────────────────
        entity.SmtpHost = dto.SmtpHost;
        entity.SmtpPort = dto.SmtpPort;
        entity.SmtpUsername = dto.SmtpUsername;
        // SmtpPassword: null = keep existing (UI didn't send it), empty = clear, non-empty = encrypt.
        // BUG FIX: Previously unconditionally overwrote SmtpPassword from DTO — if the UI
        // sent null (e.g., password field not included in the form), the password was cleared.
        if (dto.SmtpPassword != null)
            entity.SmtpPassword = _credentialProtector.Encrypt(dto.SmtpPassword);
        entity.SmtpSenderEmail = dto.SmtpSenderEmail;
        entity.SmtpSenderName = dto.SmtpSenderName;
        entity.SmtpUseSsl = dto.SmtpUseSsl;

        // ── JWT Settings ──────────────────────────────────────────────────────
        entity.JwtExpirationHours = dto.JwtExpirationHours;

        // ── AI Settings ──────────────────────────────────────────────────────
        // Same partial-update pattern as SMTP: null = keep, "" = clear, value = encrypt+update
        if (dto.AiDefaultProvider != null)
            entity.AiDefaultProvider = string.IsNullOrEmpty(dto.AiDefaultProvider) ? null : dto.AiDefaultProvider;

        if (dto.AiClaudeApiKey != null)
            entity.AiClaudeApiKey = string.IsNullOrEmpty(dto.AiClaudeApiKey) ? null : _credentialProtector.Encrypt(dto.AiClaudeApiKey);
        if (dto.AiClaudeModel != null)
            entity.AiClaudeModel = string.IsNullOrEmpty(dto.AiClaudeModel) ? null : dto.AiClaudeModel;

        if (dto.AiOpenAiApiKey != null)
            entity.AiOpenAiApiKey = string.IsNullOrEmpty(dto.AiOpenAiApiKey) ? null : _credentialProtector.Encrypt(dto.AiOpenAiApiKey);
        if (dto.AiOpenAiModel != null)
            entity.AiOpenAiModel = string.IsNullOrEmpty(dto.AiOpenAiModel) ? null : dto.AiOpenAiModel;

        if (dto.AiGeminiApiKey != null)
            entity.AiGeminiApiKey = string.IsNullOrEmpty(dto.AiGeminiApiKey) ? null : _credentialProtector.Encrypt(dto.AiGeminiApiKey);
        if (dto.AiGeminiModel != null)
            entity.AiGeminiModel = string.IsNullOrEmpty(dto.AiGeminiModel) ? null : dto.AiGeminiModel;

        if (dto.AiOllamaBaseUrl != null)
            entity.AiOllamaBaseUrl = string.IsNullOrEmpty(dto.AiOllamaBaseUrl) ? null : dto.AiOllamaBaseUrl;
        if (dto.AiOllamaModel != null)
            entity.AiOllamaModel = string.IsNullOrEmpty(dto.AiOllamaModel) ? null : dto.AiOllamaModel;

        // Azure Blob Storage — same partial-update pattern: null = keep, "" = clear, non-empty = encrypt & store
        if (dto.AzureBlobConnectionString != null)
            entity.AzureBlobConnectionString = string.IsNullOrEmpty(dto.AzureBlobConnectionString)
                ? null : _credentialProtector.Encrypt(dto.AzureBlobConnectionString);
        if (dto.AzureBlobContainerPrefix != null)
            entity.AzureBlobContainerPrefix = string.IsNullOrEmpty(dto.AzureBlobContainerPrefix)
                ? null : dto.AzureBlobContainerPrefix;
        if (dto.AzureBlobContainerName != null)
            entity.AzureBlobContainerName = string.IsNullOrEmpty(dto.AzureBlobContainerName)
                ? null : dto.AzureBlobContainerName;

        await _context.SaveChangesAsync(ct);
        _logger.LogInformation("System configuration updated successfully");

        return MapToDto(entity);
    }

    /// <summary>
    /// Gets the existing configuration row, or creates one with defaults if none exists.
    /// This ensures we always have exactly one row in the table.
    /// </summary>
    private async Task<SystemConfiguration> GetOrCreateAsync(CancellationToken ct)
    {
        // OrderBy(Id): deterministic ordering — EF warns when FirstOrDefault has no OrderBy.
        // SystemConfiguration is a singleton table, but adding OrderBy ensures no EF warning.
        var existing = await _context.Set<SystemConfiguration>().OrderBy(c => c.Id).FirstOrDefaultAsync(ct);
        if (existing != null)
            return existing;

        // No configuration row exists yet — create one with defaults
        _logger.LogInformation("No SystemConfiguration found — creating default row");
        var defaultConfig = new SystemConfiguration
        {
            SmtpHost = "",
            SmtpPort = 587,
            SmtpSenderEmail = "",
            SmtpSenderName = "Fakvio",
            SmtpUseSsl = true,
            JwtExpirationHours = 24,
            CreatedAt = DateTime.UtcNow
        };

        _context.Set<SystemConfiguration>().Add(defaultConfig);
        await _context.SaveChangesAsync(ct);
        return defaultConfig;
    }

    /// <inheritdoc />
    public async Task<string?> GetSmtpPasswordAsync(CancellationToken ct = default)
    {
        var entity = await GetOrCreateAsync(ct);
        return _credentialProtector.Decrypt(entity.SmtpPassword);
    }

    /// <inheritdoc />
    public async Task<SystemAiSettingsInternal> GetAiSettingsAsync(CancellationToken ct = default)
    {
        var entity = await GetOrCreateAsync(ct);
        return new SystemAiSettingsInternal
        {
            DefaultProvider = entity.AiDefaultProvider,
            ClaudeApiKey = _credentialProtector.Decrypt(entity.AiClaudeApiKey),
            ClaudeModel = entity.AiClaudeModel,
            OpenAiApiKey = _credentialProtector.Decrypt(entity.AiOpenAiApiKey),
            OpenAiModel = entity.AiOpenAiModel,
            GeminiApiKey = _credentialProtector.Decrypt(entity.AiGeminiApiKey),
            GeminiModel = entity.AiGeminiModel,
            OllamaBaseUrl = entity.AiOllamaBaseUrl,
            OllamaModel = entity.AiOllamaModel
        };
    }

    /// <inheritdoc />
    public async Task<BlobTestConnectionResult> TestAzureBlobConnectionAsync(CancellationToken ct = default)
    {
        // Resolve the connection string with the same fallback chain as AzureBlobFileStorage:
        // 1. SystemConfiguration (DB)
        // 2. appsettings.json
        string? connectionString = null;

        var entity = await GetOrCreateAsync(ct);

        if (!string.IsNullOrWhiteSpace(entity.AzureBlobConnectionString))
        {
            connectionString = _credentialProtector.Decrypt(entity.AzureBlobConnectionString);
            var preview = connectionString?.Length > 30
                ? connectionString[..30] + "..."
                : connectionString ?? "(null)";
            _logger.LogWarning(
                "Azure Blob test: from DB, decrypted length={Len}, starts with '{Preview}', " +
                "raw encrypted length={RawLen}",
                connectionString?.Length ?? 0, preview, entity.AzureBlobConnectionString.Length);
        }

        if (connectionString == null)
        {
            var appSettingsCs = _configuration["AzureBlobStorage:ConnectionString"];
            if (!string.IsNullOrWhiteSpace(appSettingsCs))
            {
                connectionString = appSettingsCs;
                _logger.LogDebug("Azure Blob test: using connection string from appsettings.json");
            }
        }

        // No connection string found anywhere — report configuration problem immediately
        if (string.IsNullOrWhiteSpace(connectionString))
        {
            _logger.LogWarning("Azure Blob connection test skipped — no connection string configured");
            return new BlobTestConnectionResult
            {
                Success = false,
                Error = "No Azure Blob Storage connection string is configured."
            };
        }

        try
        {
            // Lightweight test: create a BlobServiceClient and call GetProperties.
            // GetPropertiesAsync fetches service-level properties (logging, metrics config) —
            // it requires only a valid connection string and network access. No blobs are read or written.
            var serviceClient = new BlobServiceClient(connectionString);
            await serviceClient.GetPropertiesAsync(ct);

            _logger.LogInformation("Azure Blob Storage connection test succeeded");
            return new BlobTestConnectionResult { Success = true };
        }
        catch (Exception ex)
        {
            // Only log the exception type and message — do NOT log the connection string.
            _logger.LogWarning(ex, "Azure Blob Storage connection test failed: {Message}", ex.Message);
            return new BlobTestConnectionResult
            {
                Success = false,
                // Expose the error message to SysAdmin (they own the credentials), but
                // the full stack trace is in the server log only.
                Error = ex.Message
            };
        }
    }

    /// <inheritdoc />
    public async Task<CredentialHealthDto> CheckCredentialHealthAsync(CancellationToken ct = default)
    {
        var issues = new List<CredentialHealthIssueDto>();

        // ── 1. SystemConfiguration ─────────────────────────────────────────────
        // Single-row global settings in the master schema.
        var sysConfig = await GetOrCreateAsync(ct);
        CheckField(issues, "SystemConfiguration", "SmtpPassword", null, sysConfig.SmtpPassword);
        CheckField(issues, "SystemConfiguration", "AiClaudeApiKey", null, sysConfig.AiClaudeApiKey);
        CheckField(issues, "SystemConfiguration", "AiOpenAiApiKey", null, sysConfig.AiOpenAiApiKey);
        CheckField(issues, "SystemConfiguration", "AiGeminiApiKey", null, sysConfig.AiGeminiApiKey);
        CheckField(issues, "SystemConfiguration", "AzureBlobConnectionString", null, sysConfig.AzureBlobConnectionString);

        // ── 2. CompanySystemSettings (per tenant) ──────────────────────────────
        // One row per registered company. Check all companies regardless of IsActive/IsProvisioned
        // so the admin sees the full picture after a key-ring loss.
        var companySettings = await _context.CompanySystemSettings
            .AsNoTracking()
            .ToListAsync(ct);

        foreach (var company in companySettings)
        {
            var cid = company.CompanyId;
            CheckField(issues, "CompanySystemSettings", "SmtpPassword", cid, company.SmtpPassword);
            CheckField(issues, "CompanySystemSettings", "AiClaudeApiKey", cid, company.AiClaudeApiKey);
            CheckField(issues, "CompanySystemSettings", "AiOpenAiApiKey", cid, company.AiOpenAiApiKey);
            CheckField(issues, "CompanySystemSettings", "AiGeminiApiKey", cid, company.AiGeminiApiKey);
            CheckField(issues, "CompanySystemSettings", "GoogleDriveAccessToken", cid, company.GoogleDriveAccessToken);
            CheckField(issues, "CompanySystemSettings", "GoogleDriveRefreshToken", cid, company.GoogleDriveRefreshToken);
            CheckField(issues, "CompanySystemSettings", "OneDriveAccessToken", cid, company.OneDriveAccessToken);
            CheckField(issues, "CompanySystemSettings", "OneDriveRefreshToken", cid, company.OneDriveRefreshToken);
            CheckField(issues, "CompanySystemSettings", "AzureBlobConnectionString", cid, company.AzureBlobConnectionString);
        }

        // ── 3. PaymentMatchingSystemSettings ──────────────────────────────────
        // Single-row settings for the IMAP payment-matching worker.
        var paySettings = await _context.PaymentMatchingSystemSettings
            .AsNoTracking()
            .OrderBy(p => p.Id)
            .FirstOrDefaultAsync(ct);

        if (paySettings != null)
            CheckField(issues, "PaymentMatchingSystemSettings", "ImapPasswordEncrypted", null, paySettings.ImapPasswordEncrypted);

        _logger.LogInformation(
            "Credential health check completed — {IssueCount} issue(s) found",
            issues.Count);

        return new CredentialHealthDto
        {
            Healthy = issues.Count == 0,
            Issues = issues
        };
    }

    /// <summary>
    /// Helper: checks a single encrypted field and appends an issue entry when it is corrupt.
    /// Skips null/empty fields (not configured = healthy by definition).
    /// </summary>
    private void CheckField(
        List<CredentialHealthIssueDto> issues,
        string entity,
        string field,
        long? companyId,
        string? encryptedValue)
    {
        if (_credentialProtector.IsHealthy(encryptedValue))
            return;

        _logger.LogWarning(
            "Corrupt credential detected — Entity: {Entity}, Field: {Field}, CompanyId: {CompanyId}",
            entity, field, companyId?.ToString() ?? "N/A");

        issues.Add(new CredentialHealthIssueDto
        {
            Entity = entity,
            Field = field,
            CompanyId = companyId,
            Status = "corrupt"
        });
    }

    /// <summary>
    /// Maps the SystemConfiguration entity to a DTO for API responses.
    /// SmtpPassword is NOT included — only a HasSmtpPassword flag.
    /// </summary>
    private SystemConfigurationDto MapToDto(SystemConfiguration entity)
    {
        return new SystemConfigurationDto
        {
            Id = entity.Id,
            // Application settings
            AppName = entity.AppName,
            BlazorBaseUrl = entity.BlazorBaseUrl,
            // SMTP settings
            SmtpHost = entity.SmtpHost,
            SmtpPort = entity.SmtpPort,
            SmtpUsername = entity.SmtpUsername,
            // Password is NEVER sent to the UI — only a boolean flag.
            // EmailService uses GetSmtpPasswordAsync() to read the actual password internally.
            HasSmtpPassword = !string.IsNullOrEmpty(entity.SmtpPassword),
            SmtpSenderEmail = entity.SmtpSenderEmail,
            SmtpSenderName = entity.SmtpSenderName,
            SmtpUseSsl = entity.SmtpUseSsl,
            // JWT settings
            JwtExpirationHours = entity.JwtExpirationHours,
            // AI settings — API keys are NEVER exposed, only boolean flags
            AiDefaultProvider = entity.AiDefaultProvider,
            AiClaudeModel = entity.AiClaudeModel,
            HasAiClaudeApiKey = !string.IsNullOrEmpty(entity.AiClaudeApiKey),
            AiOpenAiModel = entity.AiOpenAiModel,
            HasAiOpenAiApiKey = !string.IsNullOrEmpty(entity.AiOpenAiApiKey),
            AiGeminiModel = entity.AiGeminiModel,
            HasAiGeminiApiKey = !string.IsNullOrEmpty(entity.AiGeminiApiKey),
            AiOllamaBaseUrl = entity.AiOllamaBaseUrl,
            AiOllamaModel = entity.AiOllamaModel,
            // Azure Blob Storage — connection string never exposed, only boolean flag
            HasAzureBlobConnectionString = !string.IsNullOrEmpty(entity.AzureBlobConnectionString),
            AzureBlobContainerPrefix = entity.AzureBlobContainerPrefix,
            AzureBlobContainerName = entity.AzureBlobContainerName
        };
    }
}

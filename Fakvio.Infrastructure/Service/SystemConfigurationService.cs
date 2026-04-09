using Fakvio.Application.Service;
using Fakvio.Contracts.Dto.SystemConfiguration;
using Fakvio.Domain.Entities;
using Fakvio.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
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
    private readonly ILogger<SystemConfigurationService> _logger;

    public SystemConfigurationService(
        MasterDbContext context,
        ICredentialProtector credentialProtector,
        ILogger<SystemConfigurationService> logger)
    {
        _context = context;
        _credentialProtector = credentialProtector;
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
            AzureBlobContainerPrefix = entity.AzureBlobContainerPrefix
        };
    }
}

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
            JwtExpirationHours = entity.JwtExpirationHours
        };
    }
}

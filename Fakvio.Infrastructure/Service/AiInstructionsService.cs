using Fakvio.Application.Service;
using Fakvio.Contracts.Dto.SystemConfiguration;
using Fakvio.Domain.Entities;
using Fakvio.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging;

namespace Fakvio.Infrastructure.Service;

/// <summary>
/// Manages the editable AI assistant system prompt instructions stored in SystemConfiguration.
///
/// Key design decisions:
/// - Stores in SystemConfiguration (single-row master table) — no new table needed.
/// - Caches in IMemoryCache with key AiInstructionsService.CacheKey (5 minutes sliding expiry).
/// - Cache is explicitly invalidated on every PUT/DELETE so changes take effect immediately.
/// - ChatContextBuilder calls GetCachedInstructionsAsync() — fast cache read on hot path.
/// </summary>
public class AiInstructionsService : IAiInstructionsService
{
    // Cache key used to store the instructions. Known publicly so tests can verify invalidation.
    public const string CacheKey = "AiSystemPromptInstructions";

    // Sliding expiry: cache entry refreshed on each access, expires 5 minutes after last read.
    // If ChatContextBuilder reads every few seconds (active chat), the cache stays warm.
    private static readonly TimeSpan CacheExpiry = TimeSpan.FromMinutes(5);

    private readonly MasterDbContext _context;
    private readonly IMemoryCache _cache;
    private readonly ILogger<AiInstructionsService> _logger;

    public AiInstructionsService(
        MasterDbContext context,
        IMemoryCache cache,
        ILogger<AiInstructionsService> logger)
    {
        _context = context;
        _cache = cache;
        _logger = logger;
    }

    /// <inheritdoc />
    public async Task<AiInstructionsDto> GetAsync(CancellationToken ct = default)
    {
        var config = await GetOrCreateConfigAsync(ct);
        return ToDto(config);
    }

    /// <inheritdoc />
    public async Task<AiInstructionsDto> UpdateAsync(UpdateAiInstructionsDto dto, CancellationToken ct = default)
    {
        var config = await GetOrCreateConfigAsync(ct);

        // Partial update: null = keep existing, empty string = clear (revert to default).
        if (dto.CustomPrompt != null)
            config.AiSystemPromptCustom = string.IsNullOrEmpty(dto.CustomPrompt) ? null : dto.CustomPrompt;

        if (dto.Appendix != null)
            config.AiSystemPromptAppendix = string.IsNullOrEmpty(dto.Appendix) ? null : dto.Appendix;

        await _context.SaveChangesAsync(ct);

        // Invalidate cache so the next ChatContextBuilder call reads fresh data from DB.
        _cache.Remove(CacheKey);

        _logger.LogInformation(
            "AI instructions updated — CustomPrompt={HasCustom}, Appendix={HasAppendix}",
            !string.IsNullOrEmpty(config.AiSystemPromptCustom),
            !string.IsNullOrEmpty(config.AiSystemPromptAppendix));

        return ToDto(config);
    }

    /// <inheritdoc />
    public async Task<AiInstructionsDto> ResetToDefaultAsync(CancellationToken ct = default)
    {
        var config = await GetOrCreateConfigAsync(ct);

        config.AiSystemPromptCustom = null;
        config.AiSystemPromptAppendix = null;

        await _context.SaveChangesAsync(ct);

        // Invalidate cache so hardcoded defaults take effect immediately.
        _cache.Remove(CacheKey);

        _logger.LogInformation("AI instructions reset to hardcoded defaults");

        return ToDto(config);
    }

    /// <inheritdoc />
    public async Task<AiInstructionsPreviewDto> GetPreviewAsync(CancellationToken ct = default)
    {
        var (customPrompt, appendix) = await GetCachedInstructionsAsync(ct);

        // Build a preview of the full prompt using the same logic as ChatContextBuilder,
        // but with placeholder values for the tenant-specific sections (company identity
        // and business stats require a live tenant DB which SysAdmin doesn't have here).
        var preview = BuildPreviewPrompt(customPrompt, appendix);

        return new AiInstructionsPreviewDto { FullPrompt = preview };
    }

    /// <inheritdoc />
    public async Task<(string? CustomPrompt, string? Appendix)> GetCachedInstructionsAsync(
        CancellationToken ct = default)
    {
        // Try to get from cache first (hot path — called on every AI chat message).
        if (_cache.TryGetValue(CacheKey, out (string? custom, string? appendix) cached))
            return cached;

        // Cache miss: read from DB and populate cache.
        var config = await GetOrCreateConfigAsync(ct);
        var result = (config.AiSystemPromptCustom, config.AiSystemPromptAppendix);

        var cacheOptions = new MemoryCacheEntryOptions
        {
            SlidingExpiration = CacheExpiry
        };

        _cache.Set(CacheKey, result, cacheOptions);
        return result;
    }

    /// <summary>
    /// Gets the existing SystemConfiguration row, or creates a default one.
    /// Reuses the pattern from SystemConfigurationService (same table, same row).
    /// </summary>
    private async Task<SystemConfiguration> GetOrCreateConfigAsync(CancellationToken ct)
    {
        // OrderBy ensures deterministic selection and avoids EF Core ordering warning.
        var existing = await _context.Set<SystemConfiguration>()
            .OrderBy(c => c.Id)
            .FirstOrDefaultAsync(ct);

        if (existing != null)
            return existing;

        // Fresh install — create default row.
        _logger.LogInformation("No SystemConfiguration found — creating default row for AI instructions");
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

    /// <summary>Maps entity fields to the AiInstructionsDto.</summary>
    private static AiInstructionsDto ToDto(SystemConfiguration config) => new()
    {
        CustomPrompt = config.AiSystemPromptCustom,
        Appendix = config.AiSystemPromptAppendix
    };

    /// <summary>
    /// Builds a preview of the full prompt the AI would receive, using placeholder
    /// values for the tenant-specific sections that require a live tenant DB.
    /// The structure mirrors ChatContextBuilder.BuildSystemPromptAsync().
    /// </summary>
    private static string BuildPreviewPrompt(string? customPrompt, string? appendix)
    {
        var sb = new System.Text.StringBuilder();
        sb.AppendLine("You are Fakvio AI Assistant — connected to the Fakvio invoicing system.");
        sb.AppendLine();

        // Placeholder for company identity (auto-filled at runtime from tenant DB).
        sb.AppendLine("YOUR COMPANY (the user's company — you represent this entity):");
        sb.AppendLine("- Name: [Company Name — auto from tenant DB]");
        sb.AppendLine("- IČO: [IČO — auto from tenant DB]");
        sb.AppendLine("When importing invoices: if YOUR IČO appears as the issuer (dodavatel), it's an ISSUED invoice.");
        sb.AppendLine("If YOUR IČO appears as the recipient (odběratel), it's a RECEIVED invoice.");
        sb.AppendLine();

        // Use custom prompt if set, otherwise show the hardcoded default.
        if (!string.IsNullOrEmpty(customPrompt))
        {
            sb.AppendLine("=== CUSTOM INSTRUCTIONS (SysAdmin override) ===");
            sb.AppendLine(customPrompt);
            sb.AppendLine("=== END CUSTOM INSTRUCTIONS ===");
        }
        else
        {
            sb.AppendLine("RESPONSE STYLE: Answer in ONE sentence maximum. No greetings, no filler, no repetition.");
            sb.AppendLine("Just do what the user asks and confirm the result briefly.");
            sb.AppendLine("Respond in the same language the user writes in (Czech or English).");
            sb.AppendLine();
            sb.AppendLine("TOOLS (use them, don't ask unnecessary questions):");
            sb.AppendLine("- ares_lookup / create_client / create_invoice / import_invoice");
            sb.AppendLine("- navigate / export_invoice");
            sb.AppendLine("- get_received_invoice / list_received_invoices / search_received_invoices");
            sb.AppendLine();
            sb.AppendLine("IMPORT RULES: [hardcoded defaults — see ChatContextBuilder.cs]");
            sb.AppendLine();
            sb.AppendLine("RULES: [hardcoded defaults — see ChatContextBuilder.cs]");
        }

        sb.AppendLine();

        // Appendix block (shown when set).
        if (!string.IsNullOrEmpty(appendix))
        {
            sb.AppendLine("=== ADDITIONAL INSTRUCTIONS (SysAdmin appendix) ===");
            sb.AppendLine(appendix);
            sb.AppendLine("=== END ADDITIONAL INSTRUCTIONS ===");
            sb.AppendLine();
        }

        // Placeholder for business context (auto-filled at runtime from tenant DB).
        sb.AppendLine("Current tenant business context:");
        sb.AppendLine("- Total active clients: [N/A — preview mode]");
        sb.AppendLine("- Open (unpaid) invoices: [N/A — preview mode]");
        sb.AppendLine("- Overdue invoices: [N/A — preview mode]");
        sb.AppendLine("- Paid invoices: [N/A — preview mode]");

        return sb.ToString();
    }
}

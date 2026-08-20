using Fakvio.Application.Service;
using Fakvio.Contracts.Dto.SystemConfiguration;
using Fakvio.Infrastructure.Data;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging;

namespace Fakvio.Infrastructure.Service;

/// <summary>
/// Manages the editable part of the AI assistant system prompt.
///
/// Design decisions:
/// - Stored in the existing single-row SystemConfiguration table — no new table needed.
/// - Cached in IMemoryCache, because ChatContextBuilder reads it on every chat message.
///   Without the cache every message would cost an extra master-database round-trip.
/// - The cache is invalidated explicitly on every write, so a SysAdmin edit takes effect
///   on the next message instead of waiting for the entry to expire.
/// - Read paths never write. Only Update/Reset create the configuration row when it is
///   missing; a chat message must not cause an INSERT into the master database.
/// </summary>
public class AiInstructionsService : IAiInstructionsService
{
    /// <summary>Cache key of the stored instructions. Public so tests can assert invalidation.</summary>
    public const string CacheKey = "AiSystemPromptInstructions";

    /// <summary>
    /// Sliding expiry — the entry stays warm during an active chat and is dropped shortly
    /// after the traffic stops. Writes invalidate it explicitly, so this is only a safety
    /// net against a stale entry (e.g. a row edited directly in the database).
    /// </summary>
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
        var (customPrompt, appendix) = await ReadFromDatabaseAsync(ct);
        return new AiInstructionsDto { CustomPrompt = customPrompt, Appendix = appendix };
    }

    /// <inheritdoc />
    public async Task<AiInstructionsDto> UpdateAsync(UpdateAiInstructionsDto dto, CancellationToken ct = default)
    {
        var config = await SystemConfigurationStore.GetOrCreateAsync(_context, _logger, ct);

        // Partial update, same convention as SystemConfigurationService:
        // null = the UI did not send the field, empty string = the user cleared it.
        if (dto.CustomPrompt != null)
            config.AiSystemPromptCustom = NullIfEmpty(dto.CustomPrompt);

        if (dto.Appendix != null)
            config.AiSystemPromptAppendix = NullIfEmpty(dto.Appendix);

        await _context.SaveChangesAsync(ct);
        InvalidateCache();

        // The text itself is not logged — it can be long and is not needed for diagnostics.
        _logger.LogInformation(
            "AI instructions updated — CustomPrompt={HasCustom}, Appendix={HasAppendix}",
            config.AiSystemPromptCustom != null,
            config.AiSystemPromptAppendix != null);

        return new AiInstructionsDto
        {
            CustomPrompt = config.AiSystemPromptCustom,
            Appendix = config.AiSystemPromptAppendix
        };
    }

    /// <inheritdoc />
    public async Task<AiInstructionsDto> ResetToDefaultAsync(CancellationToken ct = default)
    {
        var config = await SystemConfigurationStore.GetOrCreateAsync(_context, _logger, ct);

        config.AiSystemPromptCustom = null;
        config.AiSystemPromptAppendix = null;

        await _context.SaveChangesAsync(ct);
        InvalidateCache();

        _logger.LogInformation("AI instructions reset to the built-in defaults");

        return new AiInstructionsDto();
    }

    /// <inheritdoc />
    public async Task<AiInstructionsPreviewDto> GetPreviewAsync(CancellationToken ct = default)
    {
        var (customPrompt, appendix) = await GetCachedInstructionsAsync(ct);

        // AiSystemPrompt is the same code ChatContextBuilder uses, so the preview cannot
        // drift away from what the AI actually receives.
        return new AiInstructionsPreviewDto
        {
            FullPrompt = AiSystemPrompt.ComposePreview(customPrompt, appendix)
        };
    }

    /// <inheritdoc />
    public async Task<(string? CustomPrompt, string? Appendix)> GetCachedInstructionsAsync(
        CancellationToken ct = default)
    {
        if (_cache.TryGetValue(CacheKey, out (string? CustomPrompt, string? Appendix) cached))
            return cached;

        var fromDatabase = await ReadFromDatabaseAsync(ct);
        _cache.Set(CacheKey, fromDatabase, new MemoryCacheEntryOptions { SlidingExpiration = CacheExpiry });
        return fromDatabase;
    }

    /// <summary>
    /// Reads both parts straight from the master database. Returns nulls when the
    /// configuration row does not exist yet — a fresh install then uses the built-in
    /// defaults without any write happening on a read path.
    /// </summary>
    private async Task<(string? CustomPrompt, string? Appendix)> ReadFromDatabaseAsync(CancellationToken ct)
    {
        var config = await SystemConfigurationStore.FindAsync(_context, ct);
        return (config?.AiSystemPromptCustom, config?.AiSystemPromptAppendix);
    }

    /// <summary>Drops the cached entry so the next chat message re-reads the database.</summary>
    private void InvalidateCache() => _cache.Remove(CacheKey);

    /// <summary>Normalizes an empty edit box to null, so "no value" has one representation.</summary>
    private static string? NullIfEmpty(string value) => string.IsNullOrEmpty(value) ? null : value;
}

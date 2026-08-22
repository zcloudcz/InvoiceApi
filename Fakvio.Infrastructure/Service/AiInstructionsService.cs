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
/// - The cache is invalidated explicitly on every write, but IMemoryCache is process-local:
///   the write only clears the entry in the instance that served it. Other instances keep
///   their copy until it expires, so the entry has an absolute upper bound (see CacheExpiry).
/// - Read paths never write. Only Update/Reset create the configuration row when it is
///   missing; a chat message must not cause an INSERT into the master database.
/// </summary>
public class AiInstructionsService : IAiInstructionsService
{
    /// <summary>Cache key of the stored instructions. Public so tests can assert invalidation.</summary>
    public const string CacheKey = "AiSystemPromptInstructions";

    /// <summary>
    /// Absolute upper bound on how long a cached entry may be served. It must be absolute
    /// rather than sliding: the production host (Azure Function App) runs several instances,
    /// an explicit invalidation only reaches the one that served the write, and a sliding
    /// entry on a busy instance would be renewed by the traffic itself and never expire at
    /// all. With an absolute bound, an edit reaches every instance within this window.
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
        // null = the UI did not send the field, empty/blank string = the user cleared it.
        if (dto.CustomPrompt != null)
            config.AiSystemPromptCustom = NullIfBlank(dto.CustomPrompt);

        if (dto.Appendix != null)
            config.AiSystemPromptAppendix = NullIfBlank(dto.Appendix);

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
        // Deliberately NOT the cached accessor: the preview must show what is stored, not
        // what this instance happens to have cached. It is a rarely used SysAdmin screen,
        // so one extra master-database read is cheaper than a preview that lies.
        var (customPrompt, appendix) = await ReadFromDatabaseAsync(ct);

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
        _cache.Set(CacheKey, fromDatabase,
            new MemoryCacheEntryOptions { AbsoluteExpirationRelativeToNow = CacheExpiry });
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

    /// <summary>
    /// Drops the cached entry so the next chat message re-reads the database. Only affects
    /// this process — other instances catch up when their entry hits CacheExpiry.
    /// </summary>
    private void InvalidateCache() => _cache.Remove(CacheKey);

    /// <summary>
    /// Normalizes a blank edit box to null, so "no value" has one representation. Whitespace
    /// counts as blank on purpose: a prompt of only spaces would replace the whole built-in
    /// block with an empty line and silently strip the tool descriptions from the prompt.
    /// </summary>
    private static string? NullIfBlank(string value) => string.IsNullOrWhiteSpace(value) ? null : value;
}

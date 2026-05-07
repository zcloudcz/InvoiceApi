using Fakvio.Contracts.Dto.SystemConfiguration;

namespace Fakvio.Application.Service;

/// <summary>
/// Service for reading and updating the AI assistant system prompt instructions.
/// Instructions are stored in the SystemConfiguration table (master DB) and cached in IMemoryCache.
///
/// There are two editable parts:
/// - CustomPrompt: replaces the hardcoded style/rules sections in ChatContextBuilder.
/// - Appendix: additional rules appended after the main prompt.
///
/// Changes take effect on the next AI chat call (cache is invalidated on save).
/// SysAdmin-only — only exposed via SysAdmin-restricted API endpoints.
/// </summary>
public interface IAiInstructionsService
{
    /// <summary>
    /// Returns the current custom AI instructions (prompt and appendix) from the DB.
    /// </summary>
    Task<AiInstructionsDto> GetAsync(CancellationToken ct = default);

    /// <summary>
    /// Updates the custom AI instructions and invalidates the ChatContextBuilder cache.
    /// Partial update: null fields = keep existing, empty string = clear.
    /// </summary>
    Task<AiInstructionsDto> UpdateAsync(UpdateAiInstructionsDto dto, CancellationToken ct = default);

    /// <summary>
    /// Clears both CustomPrompt and Appendix, reverting to the hardcoded defaults.
    /// Also invalidates the ChatContextBuilder cache.
    /// </summary>
    Task<AiInstructionsDto> ResetToDefaultAsync(CancellationToken ct = default);

    /// <summary>
    /// Builds the full system prompt preview — what the AI will actually receive.
    /// Business context stats use placeholder values because preview runs without a tenant.
    /// </summary>
    Task<AiInstructionsPreviewDto> GetPreviewAsync(CancellationToken ct = default);

    /// <summary>
    /// Returns the cached custom prompt and appendix for use by ChatContextBuilder.
    /// On first call (or after cache invalidation) reads from DB and populates cache.
    /// Called frequently — must be fast (cache hit on hot path).
    /// </summary>
    Task<(string? CustomPrompt, string? Appendix)> GetCachedInstructionsAsync(CancellationToken ct = default);
}

using Fakvio.Contracts.Dto.SystemConfiguration;

namespace Fakvio.Application.Service;

/// <summary>
/// Reads and updates the editable part of the AI assistant system prompt.
///
/// The text is stored in the single-row SystemConfiguration table (master database)
/// and cached in IMemoryCache, because the chat pipeline needs it on every message.
///
/// Two editable parts:
/// - CustomPrompt — replaces the built-in style/tools/rules block.
/// - Appendix — extra rules appended after the main block.
///
/// SysAdmin-only: the only HTTP surface is the SysAdmin-restricted
/// /api/system-configuration/ai-instructions endpoint group. Changes take effect on the
/// next chat message, because saving invalidates the cache.
/// </summary>
public interface IAiInstructionsService
{
    /// <summary>Returns the currently stored instructions.</summary>
    Task<AiInstructionsDto> GetAsync(CancellationToken ct = default);

    /// <summary>
    /// Applies a partial update (null = keep, empty string = clear) and invalidates the cache.
    /// </summary>
    Task<AiInstructionsDto> UpdateAsync(UpdateAiInstructionsDto dto, CancellationToken ct = default);

    /// <summary>
    /// Clears both parts so the built-in defaults apply again, and invalidates the cache.
    /// </summary>
    Task<AiInstructionsDto> ResetToDefaultAsync(CancellationToken ct = default);

    /// <summary>
    /// Renders the full system prompt as the AI would receive it. Tenant-specific parts
    /// are placeholders — the preview runs in SysAdmin context with no tenant database.
    /// </summary>
    Task<AiInstructionsPreviewDto> GetPreviewAsync(CancellationToken ct = default);

    /// <summary>
    /// Cached read used by <c>ChatContextBuilder</c> on every chat message.
    /// Populates the cache from the database on a miss.
    /// </summary>
    Task<(string? CustomPrompt, string? Appendix)> GetCachedInstructionsAsync(CancellationToken ct = default);
}

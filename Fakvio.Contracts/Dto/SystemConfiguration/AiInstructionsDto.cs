namespace Fakvio.Contracts.Dto.SystemConfiguration;

/// <summary>
/// DTO returned by GET /api/system-configuration/ai-instructions.
/// Contains the current custom AI prompt and appendix, plus a preview flag.
/// </summary>
public class AiInstructionsDto
{
    /// <summary>
    /// Custom system prompt replacing the hardcoded style/rules sections.
    /// Null or empty means the hardcoded default is active.
    /// </summary>
    public string? CustomPrompt { get; set; }

    /// <summary>
    /// Additional instructions appended after the main prompt sections.
    /// Null or empty means no appendix is active.
    /// </summary>
    public string? Appendix { get; set; }

    /// <summary>
    /// True when CustomPrompt is non-empty (i.e., the default is overridden).
    /// Convenience flag for the UI so it can show a "using custom" indicator.
    /// </summary>
    public bool IsCustomActive => !string.IsNullOrEmpty(CustomPrompt);

    /// <summary>
    /// True when Appendix is non-empty.
    /// </summary>
    public bool IsAppendixActive => !string.IsNullOrEmpty(Appendix);
}

/// <summary>
/// DTO for PUT /api/system-configuration/ai-instructions.
/// Partial update: null = keep existing, empty = clear.
/// </summary>
public class UpdateAiInstructionsDto
{
    /// <summary>
    /// New custom prompt. Null = keep existing. Empty string = clear (revert to default).
    /// </summary>
    public string? CustomPrompt { get; set; }

    /// <summary>
    /// New appendix. Null = keep existing. Empty string = clear.
    /// </summary>
    public string? Appendix { get; set; }
}

/// <summary>
/// DTO returned by GET /api/system-configuration/ai-instructions/preview.
/// Contains the full assembled prompt text that the AI will receive.
/// </summary>
public class AiInstructionsPreviewDto
{
    /// <summary>
    /// The complete system prompt as it will be sent to the AI.
    /// Includes company identity, custom/default rules, appendix, and business context.
    /// Business context stats contain placeholder values ("N/A") because preview runs
    /// outside a tenant context.
    /// </summary>
    public string FullPrompt { get; set; } = "";
}

namespace Fakvio.Contracts.Dto.SystemConfiguration;

/// <summary>
/// Current state of the editable AI assistant instructions.
/// Returned by GET /api/system-configuration/ai-instructions.
/// </summary>
public class AiInstructionsDto
{
    /// <summary>
    /// Custom instructions replacing the built-in style/tools/rules block.
    /// Null or blank means the built-in default is active.
    /// </summary>
    public string? CustomPrompt { get; set; }

    /// <summary>
    /// Extra instructions appended after the main block.
    /// Null or blank means no appendix is active.
    /// </summary>
    public string? Appendix { get; set; }

    /// <summary>
    /// True when <see cref="CustomPrompt"/> carries real text, i.e. the built-in default
    /// is overridden. Convenience flag so the UI can render a status chip.
    /// Whitespace-only counts as "not set" — same rule the prompt composition uses.
    /// </summary>
    public bool IsCustomActive => !string.IsNullOrWhiteSpace(CustomPrompt);

    /// <summary>True when <see cref="Appendix"/> carries real text (whitespace = not set).</summary>
    public bool IsAppendixActive => !string.IsNullOrWhiteSpace(Appendix);
}

/// <summary>
/// Partial update payload for PUT /api/system-configuration/ai-instructions.
/// Null = keep the stored value, empty string = clear it.
/// </summary>
public class UpdateAiInstructionsDto
{
    /// <summary>
    /// New custom instructions. Null = keep existing, empty string = clear
    /// (which re-activates the built-in default).
    /// </summary>
    public string? CustomPrompt { get; set; }

    /// <summary>New appendix. Null = keep existing, empty string = clear.</summary>
    public string? Appendix { get; set; }
}

/// <summary>
/// Rendered system prompt returned by
/// GET /api/system-configuration/ai-instructions/preview.
/// </summary>
public class AiInstructionsPreviewDto
{
    /// <summary>
    /// The complete system prompt exactly as the AI would receive it, except that the
    /// tenant-specific parts (company identity, business statistics) are rendered as
    /// placeholders — the preview runs in SysAdmin context with no tenant database.
    /// </summary>
    public string FullPrompt { get; set; } = "";
}

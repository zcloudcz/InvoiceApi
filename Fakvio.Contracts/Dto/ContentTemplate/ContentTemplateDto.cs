using Fakvio.Domain.Enums;

namespace Fakvio.Contracts.Dto.ContentTemplate;

/// <summary>
/// DTO for reading content template data.
/// Used in API responses when returning template information.
/// </summary>
public class ContentTemplateDto
{
    public long Id { get; set; }
    public string Name { get; set; } = string.Empty;
    public string? Subject { get; set; }
    public string HtmlBody { get; set; } = string.Empty;
    public EContentTemplateType TemplateType { get; set; }
    public bool IsDefault { get; set; }
    public bool IsActive { get; set; }
    public string? Description { get; set; }

    /// <summary>
    /// Language this template is written in — ISO 639-1 code (e.g., "cs", "en").
    /// </summary>
    public string Language { get; set; } = "cs";

    public DateTime CreatedAt { get; set; }
    public DateTime? UpdatedAt { get; set; }
}

/// <summary>
/// DTO for creating a new content template.
/// </summary>
public class CreateContentTemplateDto
{
    public string Name { get; set; } = string.Empty;

    /// <summary>
    /// Email subject line — required for email templates, null/ignored for PDF templates.
    /// </summary>
    public string? Subject { get; set; }

    public string HtmlBody { get; set; } = string.Empty;
    public EContentTemplateType TemplateType { get; set; }
    public bool IsDefault { get; set; }

    /// <summary>
    /// Optional description / usage notes.
    /// </summary>
    public string? Description { get; set; }

    /// <summary>
    /// Language this template is written in — ISO 639-1 code. Defaults to "cs".
    /// </summary>
    public string Language { get; set; } = "cs";
}

/// <summary>
/// DTO for updating an existing content template.
/// All fields are optional — only provided fields will be updated.
/// </summary>
public class UpdateContentTemplateDto
{
    public string? Name { get; set; }
    public string? Subject { get; set; }
    public string? HtmlBody { get; set; }
    public EContentTemplateType? TemplateType { get; set; }
    public bool? IsDefault { get; set; }
    public bool? IsActive { get; set; }
    public string? Description { get; set; }

    /// <summary>
    /// Language this template is written in — ISO 639-1 code. Null means "don't change".
    /// </summary>
    public string? Language { get; set; }
}

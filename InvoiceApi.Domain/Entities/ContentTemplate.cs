using InvoiceApi.Domain.Common;
using InvoiceApi.Domain.Enums;

namespace InvoiceApi.Domain.Entities;

/// <summary>
/// Unified content template entity for ALL HTML outputs in the system:
/// PDF templates (invoice PDF, credit note PDF) and email templates (invoice email,
/// invitation email, reminder email, etc.).
///
/// Templates use Handlebars-style placeholders (e.g., {{CompanyName}}, {{InvoiceNumber}})
/// that are replaced with actual data at render time.
///
/// This replaces the old EmailTemplate entity by adding PDF template support
/// and providing a single unified system for all template management.
/// </summary>
public class ContentTemplate : BaseEntity
{
    /// <summary>
    /// Human-readable template name (e.g., "Default Invoice PDF", "Invoice Email Template")
    /// </summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>
    /// Email subject line with optional Handlebars placeholders.
    /// Null for PDF-only templates (they don't have a subject).
    /// Example: "Invoice {{InvoiceNumber}} from {{CompanyName}}"
    /// </summary>
    public string? Subject { get; set; }

    /// <summary>
    /// HTML body content with Handlebars placeholders.
    /// For PDF templates: full HTML document with styles.
    /// For email templates: HTML email body.
    /// Rendered and substituted at export/send time.
    /// </summary>
    public string HtmlBody { get; set; } = string.Empty;

    /// <summary>
    /// What this template is for — determines available placeholders and rendering pipeline.
    /// Groups: 1-9 PDF, 10-19 document emails, 20-29 system emails.
    /// </summary>
    public EContentTemplateType TemplateType { get; set; }

    /// <summary>
    /// Whether this is the default template for its TemplateType.
    /// Only one template per TemplateType should be marked as default.
    /// The system picks the default template when no specific template ID is provided.
    /// </summary>
    public bool IsDefault { get; set; }

    /// <summary>
    /// Whether this template is active and available for use.
    /// Soft delete — setting to false hides the template without removing data.
    /// </summary>
    public bool IsActive { get; set; } = true;

    /// <summary>
    /// Optional description/usage notes for this template.
    /// Helps users understand what placeholders are available and when to use it.
    /// </summary>
    public string? Description { get; set; }
}

namespace Fakvio.Domain.Entities;

/// <summary>
/// Invoice template - inherits all properties from Invoice
/// Used for creating recurring or standardized invoices
/// Stored in same table as Invoice using TPH (Table-Per-Hierarchy)
/// </summary>
public class InvoiceTemplate : Invoice
{
    /// <summary>
    /// Template name (e.g., "Monthly Hosting", "Standard Consultation")
    /// REQUIRED for templates
    /// </summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>
    /// Optional description/notes about this template
    /// Helps users understand when to use this template
    /// </summary>
    public string? Description { get; set; }

    /// <summary>
    /// Due date offset in days (used when creating invoice from template)
    /// Example: 14 means invoice due date = issue date + 14 days
    /// Default: 14 days
    /// </summary>
    public int DueDateOffsetDays { get; set; } = 14;

    /// <summary>
    /// Category for organizing templates
    /// Examples: "Hosting", "Consulting", "Retail", "Services"
    /// Helps group templates in UI
    /// </summary>
    public string? Category { get; set; }

    /// <summary>
    /// How many times this template has been used
    /// Incremented each time an invoice is created from this template
    /// Useful for analytics and identifying popular templates
    /// </summary>
    public int UsageCount { get; set; } = 0;

    /// <summary>
    /// Last time this template was used to create an invoice
    /// NULL if never used
    /// </summary>
    public DateTime? LastUsedAt { get; set; }

    /// <summary>
    /// Is this template active/available for use?
    /// Inactive templates don't show in lists by default
    /// Soft delete - keeps template data for historical invoices
    /// </summary>
    public bool IsActive { get; set; } = true;

    /// <summary>
    /// Optional number sequence override for invoices created from this template.
    /// If null, the default sequence for the document type is used.
    /// Allows different templates to use different numbering schemes.
    /// </summary>
    public long? NumberSequenceId { get; set; }

    /// <summary>
    /// Navigation property to the optional number sequence.
    /// </summary>
    public NumberSequence? NumberSequence { get; set; }

    // NOTE: HtmlTemplate was removed from InvoiceTemplate.
    // PDF rendering templates are now managed via the ContentTemplate entity
    // (EContentTemplateType.InvoicePdf / CreditNotePdf).
    // InvoiceTemplate is now purely a data template (invoice blueprint) for quick invoice creation.
}

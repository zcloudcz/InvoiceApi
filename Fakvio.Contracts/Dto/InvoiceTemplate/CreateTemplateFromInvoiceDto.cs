using System.ComponentModel.DataAnnotations;

namespace Fakvio.Contracts.Dto.InvoiceTemplate;

/// <summary>
/// DTO for creating an invoice template from an existing invoice.
/// Sent as the request body to POST /api/invoicetemplate/from-invoice/{invoiceId}.
/// Matches the API controller's CreateTemplateFromInvoiceRequest shape.
/// </summary>
public class CreateTemplateFromInvoiceDto
{
    /// <summary>
    /// Name for the new template (required, max 200 chars)
    /// </summary>
    [Required]
    [StringLength(200)]
    public string TemplateName { get; set; } = string.Empty;

    /// <summary>
    /// Optional description of when/how to use this template
    /// </summary>
    [StringLength(2000)]
    public string? Description { get; set; }

    /// <summary>
    /// Optional category for organizing templates (e.g. "Hosting", "Services")
    /// </summary>
    [StringLength(100)]
    public string? Category { get; set; }
}

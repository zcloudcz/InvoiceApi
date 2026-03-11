using System.ComponentModel.DataAnnotations;

namespace Fakvio.Contracts.Dto.Import;

/// <summary>
/// Target type for invoice import — determines validation rules and which service to call.
/// </summary>
public enum EImportTarget
{
    /// <summary>Import as issued invoice (faktura vydaná). Validates issuer = active company.</summary>
    IssuedInvoice = 1,
    /// <summary>Import as received invoice (faktura přijatá). Validates recipient = active company.</summary>
    ReceivedInvoice = 2
}

/// <summary>
/// Severity levels for import validation messages.
/// Error = blocks import, Warning = user should review, Info = informational only.
/// </summary>
public enum EImportValidationSeverity
{
    Info = 1,
    Warning = 2,
    Error = 3
}

/// <summary>
/// A single validation message produced during import preview.
/// Shown to the user so they can fix issues before confirming the import.
/// </summary>
public class ImportValidationMessage
{
    /// <summary>Which field the validation applies to (e.g., "DocumentNumber", "Client").</summary>
    public string Field { get; set; } = string.Empty;

    /// <summary>Human-readable message describing the issue.</summary>
    public string Message { get; set; } = string.Empty;

    /// <summary>Severity: Error blocks import, Warning requires review.</summary>
    public EImportValidationSeverity Severity { get; set; }
}

/// <summary>
/// Preview result for a single PDF file import.
/// Contains extracted data, validation results, and client resolution.
/// The user reviews this before confirming the import.
/// </summary>
public class InvoiceImportPreviewDto
{
    /// <summary>Original file name of the uploaded PDF.</summary>
    public string FileName { get; set; } = string.Empty;

    /// <summary>Whether a QR code was found in the PDF.</summary>
    public bool HasQrCode { get; set; }

    /// <summary>Type of QR code found: "SIND", "SPD", or null.</summary>
    public string? QrType { get; set; }

    /// <summary>Which extraction method produced the data: "QrCode", "AiExtraction", "RegexFallback", "Merged".</summary>
    public string ExtractionSource { get; set; } = string.Empty;

    // ─── Extracted data (user can edit before confirming) ─────────────────

    public string? DocumentNumber { get; set; }
    public DateTime? IssueDate { get; set; }
    public DateTime? DueDate { get; set; }
    public DateTime? TaxableSupplyDate { get; set; }
    public decimal? TotalAmount { get; set; }
    public decimal? TotalVat { get; set; }
    public decimal? TotalBeforeVat { get; set; }
    public string? Currency { get; set; }
    public string? VariableSymbol { get; set; }
    public string? IBAN { get; set; }
    public string? SWIFT { get; set; }
    public string? BankAccountNumber { get; set; }
    public string? PaymentMethod { get; set; }

    // ─── Issuer/Supplier identification ──────────────────────────────────

    public string? IssuerName { get; set; }
    public string? IssuerRegistrationNumber { get; set; }
    public string? IssuerTaxNumber { get; set; }

    // ─── Recipient identification ────────────────────────────────────────

    public string? RecipientName { get; set; }
    public string? RecipientRegistrationNumber { get; set; }
    public string? RecipientTaxNumber { get; set; }

    // ─── Client resolution ───────────────────────────────────────────────

    /// <summary>Resolved client ID (null if client not found in DB).</summary>
    public long? ResolvedClientId { get; set; }

    /// <summary>Name of the resolved client.</summary>
    public string? ResolvedClientName { get; set; }

    /// <summary>True if the client doesn't exist and needs to be created.</summary>
    public bool ClientNeedsCreation { get; set; }

    // ─── Extracted line items (AI-only) ──────────────────────────────────

    public List<ImportInvoiceItemDto>? Items { get; set; }

    // ─── Validation ──────────────────────────────────────────────────────

    /// <summary>Validation messages (errors, warnings, info).</summary>
    public List<ImportValidationMessage> Validations { get; set; } = new();

    /// <summary>True if there are no Error-level validations (import can proceed).</summary>
    public bool IsValid => !Validations.Any(v => v.Severity == EImportValidationSeverity.Error);
}

/// <summary>
/// A single line item extracted from a PDF invoice (typically by AI).
/// </summary>
public class ImportInvoiceItemDto
{
    public string? Description { get; set; }
    public decimal? Quantity { get; set; }
    public decimal? UnitPrice { get; set; }
    public decimal? VatRate { get; set; }
    public string? Unit { get; set; }
}

/// <summary>
/// Request to confirm the import of one or more invoices after preview review.
/// Sent by the user after they've reviewed and optionally edited the previewed data.
/// </summary>
public class ConfirmInvoiceImportRequest
{
    /// <summary>Target: IssuedInvoice or ReceivedInvoice.</summary>
    [Required]
    public EImportTarget Target { get; set; }

    /// <summary>List of invoices to import (one per uploaded PDF).</summary>
    [Required, MinLength(1)]
    public List<ConfirmImportItemDto> Items { get; set; } = new();
}

/// <summary>
/// A single invoice to import, with user-confirmed/edited values.
/// </summary>
public class ConfirmImportItemDto
{
    /// <summary>Original file name (for correlation with preview).</summary>
    public string FileName { get; set; } = string.Empty;

    /// <summary>PDF file bytes (base64-decoded from the upload).</summary>
    public byte[] PdfBytes { get; set; } = [];

    // ─── User-confirmed invoice data ─────────────────────────────────────

    public string? DocumentNumber { get; set; }
    public DateTime? IssueDate { get; set; }
    public DateTime? DueDate { get; set; }
    public DateTime? TaxableSupplyDate { get; set; }
    public decimal? TotalAmount { get; set; }
    public decimal? TotalVat { get; set; }
    public decimal? TotalBeforeVat { get; set; }
    public string? Currency { get; set; }
    public string? VariableSymbol { get; set; }
    public string? IBAN { get; set; }
    public string? SWIFT { get; set; }
    public string? BankAccountNumber { get; set; }
    public string? PaymentMethod { get; set; }

    // ─── Client resolution ───────────────────────────────────────────────

    /// <summary>Existing client ID to assign (null if creating new).</summary>
    public long? ClientId { get; set; }

    /// <summary>True to auto-create the client from IČO (ARES lookup).</summary>
    public bool CreateClient { get; set; }

    /// <summary>IČO for new client creation.</summary>
    public string? NewClientRegistrationNumber { get; set; }

    // ─── Line items ──────────────────────────────────────────────────────

    public List<ImportInvoiceItemDto>? Items { get; set; }
}

/// <summary>
/// Result of a confirmed import — one entry per successfully imported invoice.
/// </summary>
public class ImportResultDto
{
    /// <summary>Original file name.</summary>
    public string FileName { get; set; } = string.Empty;

    /// <summary>Whether this invoice was imported successfully.</summary>
    public bool Success { get; set; }

    /// <summary>Error message if import failed.</summary>
    public string? ErrorMessage { get; set; }

    /// <summary>ID of the created invoice (issued or received).</summary>
    public long? InvoiceId { get; set; }

    /// <summary>Document number of the created invoice.</summary>
    public string? DocumentNumber { get; set; }

    /// <summary>ID of a newly created client (if applicable).</summary>
    public long? CreatedClientId { get; set; }
}

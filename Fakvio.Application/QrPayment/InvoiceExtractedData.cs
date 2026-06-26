namespace Fakvio.Application.QrPayment;

/// <summary>
/// Unified data model for invoice data extracted from any source:
/// QR code (SIND/SPD), AI extraction, or regex text parsing.
///
/// All fields are nullable because no single extraction method guarantees
/// all fields will be found. The import orchestrator merges data from
/// multiple sources (QR > AI > Regex) to maximize field coverage.
///
/// Junior note: This is a "data bag" — it holds extracted values without
/// any business logic. Validation happens in InvoiceImportService.
/// </summary>
public class InvoiceExtractedData
{
    // ─── Document identification ─────────────────────────────────────────

    /// <summary>
    /// Invoice/document number (e.g., "FV2026001").
    /// From SIND: ID attribute. From text: regex for "Faktura č."
    /// </summary>
    public string? DocumentNumber { get; set; }

    /// <summary>
    /// Date when the invoice was issued.
    /// From SIND: DD attribute (YYYYMMDD). From text: "Datum vystavení".
    /// </summary>
    public DateTime? IssueDate { get; set; }

    /// <summary>
    /// Payment due date.
    /// From SIND: DT attribute. From text: "Datum splatnosti".
    /// </summary>
    public DateTime? DueDate { get; set; }

    /// <summary>
    /// Date of taxable supply (DUZP) — determines the VAT period.
    /// From SIND: DUZP attribute. From text: "DUZP" or "Datum uskutečnění zdanitelného plnění".
    /// </summary>
    public DateTime? TaxableSupplyDate { get; set; }

    // ─── Amounts ─────────────────────────────────────────────────────────

    /// <summary>
    /// Total amount including VAT (the amount to pay).
    /// From SIND: AM attribute. From text: "Celkem k úhradě".
    /// </summary>
    public decimal? TotalAmount { get; set; }

    /// <summary>
    /// Total VAT amount. Not available in SIND — extracted by AI or regex.
    /// </summary>
    public decimal? TotalVat { get; set; }

    /// <summary>
    /// Total before VAT (tax base). Not directly in SIND — can be computed
    /// from VAT breakdown (TB0+TB1+TB2+NTB) or extracted by AI/regex.
    /// </summary>
    public decimal? TotalBeforeVat { get; set; }

    /// <summary>
    /// ISO 4217 currency code (e.g., "CZK", "EUR").
    /// From SIND: CC attribute. Defaults to CZK if not specified.
    /// </summary>
    public string? Currency { get; set; }

    // ─── Payment details ─────────────────────────────────────────────────

    /// <summary>
    /// Variable symbol — Czech payment identifier (max 10 digits).
    /// From SIND: VS attribute. From SPD: X-VS attribute.
    /// </summary>
    public string? VariableSymbol { get; set; }

    /// <summary>
    /// IBAN of the payee.
    /// From SIND/SPD: ACC attribute (before the + separator).
    /// </summary>
    public string? IBAN { get; set; }

    /// <summary>
    /// SWIFT/BIC code of the payee's bank.
    /// From SIND/SPD: ACC attribute (after the + separator).
    /// </summary>
    public string? SWIFT { get; set; }

    /// <summary>
    /// Czech bank account number format (e.g., "123456-1234567890/0100").
    /// Not in SIND/SPD — extracted by AI or regex from text.
    /// </summary>
    public string? BankAccountNumber { get; set; }

    /// <summary>
    /// Payment method if detectable (BankTransfer, Cash, etc.).
    /// Typically only extracted by AI — not in SIND/SPD format.
    /// </summary>
    public string? PaymentMethod { get; set; }

    // ─── Issuer (supplier) identification ────────────────────────────────

    /// <summary>
    /// Company name of the invoice issuer (supplier).
    /// Not in SIND — extracted by AI or regex.
    /// </summary>
    public string? IssuerName { get; set; }

    /// <summary>
    /// Issuer's registration number (IČO — 8 digits in Czech Republic).
    /// From SIND: INI attribute.
    /// </summary>
    public string? IssuerRegistrationNumber { get; set; }

    /// <summary>
    /// Issuer's tax number (DIČ — e.g., "CZ12345678").
    /// From SIND: VII attribute.
    /// </summary>
    public string? IssuerTaxNumber { get; set; }

    // ─── Recipient (customer) identification ─────────────────────────────

    /// <summary>
    /// Company name of the invoice recipient (customer).
    /// Not in SIND — extracted by AI or regex.
    /// </summary>
    public string? RecipientName { get; set; }

    /// <summary>
    /// Recipient's registration number (IČO).
    /// From SIND: INR attribute.
    /// </summary>
    public string? RecipientRegistrationNumber { get; set; }

    /// <summary>
    /// Recipient's tax number (DIČ).
    /// From SIND: VIR attribute.
    /// </summary>
    public string? RecipientTaxNumber { get; set; }

    // ─── VAT breakdown (from SIND) ───────────────────────────────────────

    /// <summary>
    /// Tax base for standard (basic) VAT rate. From SIND: TB0 attribute.
    /// </summary>
    public decimal? StandardVatBase { get; set; }

    /// <summary>
    /// Tax amount for standard VAT rate. From SIND: T0 attribute.
    /// </summary>
    public decimal? StandardVatAmount { get; set; }

    /// <summary>
    /// Tax base for first reduced VAT rate. From SIND: TB1 attribute.
    /// </summary>
    public decimal? ReducedVat1Base { get; set; }

    /// <summary>
    /// Tax amount for first reduced VAT rate. From SIND: T1 attribute.
    /// </summary>
    public decimal? ReducedVat1Amount { get; set; }

    /// <summary>
    /// Tax base for second reduced VAT rate. From SIND: TB2 attribute.
    /// </summary>
    public decimal? ReducedVat2Base { get; set; }

    /// <summary>
    /// Tax amount for second reduced VAT rate. From SIND: T2 attribute.
    /// </summary>
    public decimal? ReducedVat2Amount { get; set; }

    /// <summary>
    /// Non-taxable amount (for VAT-exempt items). From SIND: NTB attribute.
    /// </summary>
    public decimal? NonTaxableAmount { get; set; }

    // ─── Line items (AI-only — not available from QR or regex) ───────────

    /// <summary>
    /// Individual invoice line items. Only populated by AI extraction.
    /// QR codes don't contain line items, and regex is too unreliable for table parsing.
    /// Null means items were not extracted (not the same as empty list = no items).
    /// </summary>
    public List<ExtractedInvoiceItem>? Items { get; set; }

    // ─── Document type ──────────────────────────────────────────────────

    /// <summary>
    /// Detected document type: Invoice, CreditNote, Proforma, TaxReceiptForAdvance.
    /// Null means not detected — defaults to Invoice.
    /// From ISDOC: DocumentType element (1=Invoice, 2=CreditNote, 4=ProformaInvoice, etc.).
    /// From AI: explicit field in extraction prompt.
    /// </summary>
    public string? DetectedDocumentType { get; set; }

    // ─── Metadata ────────────────────────────────────────────────────────

    /// <summary>
    /// Which extraction method produced this data.
    /// Used for logging and UI display (e.g., show QR icon if from QR).
    /// </summary>
    public EExtractionSource Source { get; set; }
}

/// <summary>
/// A single line item extracted from the invoice (typically by AI).
/// All fields are nullable because AI extraction is best-effort.
/// </summary>
public class ExtractedInvoiceItem
{
    public string? Description { get; set; }
    public decimal? Quantity { get; set; }
    public decimal? UnitPrice { get; set; }
    public decimal? VatRate { get; set; }
    public string? Unit { get; set; }
    public string? ProductCode { get; set; }
}

/// <summary>
/// Indicates which extraction method produced the InvoiceExtractedData.
/// Used for logging, UI icons, and merge priority decisions.
/// </summary>
public enum EExtractionSource
{
    /// <summary>Data extracted from QR code (SIND/SPD) — highest reliability.</summary>
    QrCode = 1,

    /// <summary>Data extracted by AI provider — high reliability, may include line items.</summary>
    AiExtraction = 2,

    /// <summary>Data extracted by regex patterns — lowest reliability, fallback only.</summary>
    RegexFallback = 3,

    /// <summary>Data merged from multiple sources (e.g., QR + AI fill-in).</summary>
    Merged = 4
}

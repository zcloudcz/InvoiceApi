using Fakvio.Domain.Enums;
using MudBlazor;

namespace Fakvio.UI.Shared.Models;

/// <summary>
/// THE one place where the UI decides anything by document type (issued invoice, credit note,
/// proforma, tax receipt, received invoice). Pages and grids ask this policy instead of
/// switching on the type themselves, so a new rule is added here once and covered by one test.
///
/// It is immutable, has no DI and no localizer: it only returns resource KEYS (the page does
/// <c>L[key]</c>), urls and flags. That keeps it unit-testable without rendering anything.
/// Get an instance with <see cref="For"/> or <see cref="ForRoute"/>.
/// </summary>
public sealed class DocumentTypeUiPolicy
{
    // One cached instance per type - the policy is stateless, so sharing is safe.
    private static readonly Dictionary<EDocumentType, DocumentTypeUiPolicy> Cache =
        Enum.GetValues<EDocumentType>().ToDictionary(t => t, t => new DocumentTypeUiPolicy(t));

    private DocumentTypeUiPolicy(EDocumentType type) => Type = type;

    /// <summary>The document type this policy describes.</summary>
    public EDocumentType Type { get; }

    /// <summary>Returns the policy for the given type; throws for a value that is not a defined type.</summary>
    public static DocumentTypeUiPolicy For(EDocumentType type) =>
        Cache.TryGetValue(type, out var policy)
            ? policy
            : throw new ArgumentOutOfRangeException(nameof(type), type, "Unknown document type.");

    /// <summary>
    /// Resolves the policy of the list page from its URL. The type is FIXED by the url: the path
    /// <c>/received-invoices</c> is always ReceivedInvoice, <c>/invoices</c> is Invoice or the issued
    /// type named by <c>?type=</c> (unknown or missing value = plain Invoice, as before).
    /// Returns null when the path is neither list route (e.g. the user already navigated to a detail page).
    /// </summary>
    public static DocumentTypeUiPolicy? ForListRoute(string absolutePath, string? typeFilter)
    {
        var path = absolutePath.TrimEnd('/');

        if (path.EndsWith("/received-invoices", StringComparison.OrdinalIgnoreCase))
            return For(EDocumentType.ReceivedInvoice);

        if (!path.EndsWith("/invoices", StringComparison.OrdinalIgnoreCase))
            return null;

        return For(typeFilter switch
        {
            "CreditNote" => EDocumentType.CreditNote,
            "Proforma" => EDocumentType.Proforma,
            "TaxReceiptForAdvance" => EDocumentType.TaxReceiptForAdvance,
            _ => EDocumentType.Invoice
        });
    }

    // ── Kind ────────────────────────────────────────────────────────────────

    /// <summary>True for a received (supplier) invoice, false for every issued document.</summary>
    public bool IsReceived => Type == EDocumentType.ReceivedInvoice;

    // ── Urls ────────────────────────────────────────────────────────────────

    // "?type=..." suffix that keeps the list/create page on the right issued type.
    private string TypeQuery => Type switch
    {
        EDocumentType.CreditNote => "?type=CreditNote",
        EDocumentType.Proforma => "?type=Proforma",
        EDocumentType.TaxReceiptForAdvance => "?type=TaxReceiptForAdvance",
        _ => ""
    };

    private string BasePath => IsReceived ? "/received-invoices" : "/invoices";

    /// <summary>
    /// Key of the grid on the list page. Issued and received grids keep their own saved column
    /// state, and a changed key makes the page re-create the grid when the user switches sides.
    /// </summary>
    public string GridKey => IsReceived ? "received-invoices" : "invoices";

    /// <summary>List page of this type.</summary>
    public string ListUrl => BasePath + TypeQuery;

    /// <summary>Create page of this type.</summary>
    public string CreateUrl => BasePath + "/create" + TypeQuery;

    /// <summary>Detail page of one document.</summary>
    public string DetailUrl(long id) => $"{BasePath}/{id}";

    /// <summary>Import wizard pre-set to this side (issued or received).</summary>
    public string ImportUrl => IsReceived
        ? "/received-invoices/import?target=ReceivedInvoice"
        : "/invoices/import?target=IssuedInvoice";

    // ── Texts (resource keys) ───────────────────────────────────────────────

    /// <summary>List page heading.</summary>
    public string ListTitleKey => Type switch
    {
        EDocumentType.CreditNote => "Nav_CreditNotes",
        EDocumentType.Proforma => "Proforma_Title",
        EDocumentType.TaxReceiptForAdvance => "TaxReceiptForAdvance_Title",
        EDocumentType.ReceivedInvoice => "ReceivedInvoice_Title",
        _ => "Invoice_Title"
    };

    /// <summary>List page subtitle; null = the page shows none (received invoices).</summary>
    public string? ListSubtitleKey => Type switch
    {
        EDocumentType.CreditNote => "CreditNote_Subtitle",
        EDocumentType.Proforma => "Proforma_Subtitle",
        EDocumentType.TaxReceiptForAdvance => "TaxReceiptForAdvance_Subtitle",
        EDocumentType.ReceivedInvoice => null,
        _ => "Invoice_Subtitle"
    };

    /// <summary>Label of the "new document" button.</summary>
    public string NewLabelKey => Type switch
    {
        EDocumentType.CreditNote => "CreditNote_New",
        EDocumentType.Proforma => "Proforma_New",
        EDocumentType.TaxReceiptForAdvance => "TaxReceiptForAdvance_New",
        EDocumentType.ReceivedInvoice => "ReceivedInvoice_New",
        _ => "Invoice_New"
    };

    /// <summary>Heading of the detail page of an existing document.</summary>
    public string DetailTitleKey => Type switch
    {
        EDocumentType.CreditNote => "CreditNote_Detail",
        EDocumentType.Proforma => "Proforma_Detail",
        EDocumentType.TaxReceiptForAdvance => "TaxReceiptForAdvance_Detail",
        EDocumentType.ReceivedInvoice => "ReceivedInvoice_Detail",
        _ => "Invoice_Detail"
    };

    /// <summary>Heading of the detail page in create mode (same text as the "new" button).</summary>
    public string CreateTitleKey => NewLabelKey;

    /// <summary>Label of the counterparty column/field: client for issued, supplier for received.</summary>
    public string CounterpartyLabelKey => IsReceived ? "ReceivedInvoice_Supplier" : "Invoice_Client";

    // ── Status presentation ─────────────────────────────────────────────────

    /// <summary>
    /// Localization key of the status chip text. Draft/Completed are worded differently for
    /// received invoices ("Received"/"Approved" instead of "Draft"/"Issued"); the rest is shared.
    /// </summary>
    public string StatusLabelKey(EInvoiceStatus status) => status switch
    {
        EInvoiceStatus.Draft => IsReceived ? "EReceivedInvoiceStatus_Received" : "Invoice_StatusDraft",
        EInvoiceStatus.Completed => IsReceived ? "EReceivedInvoiceStatus_Approved" : "Invoice_StatusIssued",
        EInvoiceStatus.Paid => "Invoice_StatusPaid",
        EInvoiceStatus.Creditnoted => "Invoice_StatusCredited",
        EInvoiceStatus.Deleted => "Invoice_StatusDeleted",
        EInvoiceStatus.PartiallyPaid => "Invoice_StatusPartiallyPaid",
        _ => throw new ArgumentOutOfRangeException(nameof(status), status, "Unknown invoice status.")
    };

    /// <summary>Status chip color - one mapping for every document type.</summary>
    public static Color StatusColor(EInvoiceStatus status) => status switch
    {
        EInvoiceStatus.Draft => Color.Default,
        EInvoiceStatus.Completed => Color.Info,
        EInvoiceStatus.Paid => Color.Success,
        EInvoiceStatus.Creditnoted => Color.Warning,
        EInvoiceStatus.Deleted => Color.Dark,
        EInvoiceStatus.PartiallyPaid => Color.Info,
        _ => throw new ArgumentOutOfRangeException(nameof(status), status, "Unknown invoice status.")
    };

    // ── "Complete" action (issue for issued documents, approve for received) ──

    /// <summary>Row action label/tooltip.</summary>
    public string CompleteLabelKey => IsReceived ? "ReceivedInvoice_Approve" : "Invoice_Issue";

    /// <summary>Label of the bulk toolbar button.</summary>
    public string BulkCompleteLabelKey => IsReceived ? "ReceivedInvoice_Approve" : "Bulk_Issue";

    /// <summary>Snackbar text after a successful single complete.</summary>
    public string CompleteSuccessKey => IsReceived ? "ReceivedInvoice_Approved" : "Invoice_Issued";

    /// <summary>Confirmation text of the bulk complete ({0} = count).</summary>
    public string BulkConfirmCompleteKey => IsReceived ? "Bulk_ConfirmApprove" : "Bulk_ConfirmIssue";

    // ── PDF / files ─────────────────────────────────────────────────────────

    /// <summary>Content template type offered in the PDF template picker; null = no PDF for this type.</summary>
    public EContentTemplateType? PdfTemplateType => Type switch
    {
        EDocumentType.Invoice => EContentTemplateType.InvoicePdf,
        EDocumentType.CreditNote => EContentTemplateType.CreditNotePdf,
        EDocumentType.Proforma => EContentTemplateType.AdvanceInvoicePdf,
        EDocumentType.TaxReceiptForAdvance => EContentTemplateType.TaxReceiptForAdvancePdf,
        _ => null
    };

    /// <summary>Resource key of the file-name prefix of the bulk PDF ZIP; null = no bulk PDF.</summary>
    public string? BulkPdfZipPrefixKey => Type switch
    {
        EDocumentType.Invoice => "FileName_Invoices",
        EDocumentType.CreditNote => "FileName_CreditNotes",
        EDocumentType.Proforma => "Proforma_Title",
        EDocumentType.TaxReceiptForAdvance => "TaxReceiptForAdvance_Title",
        _ => null
    };

    /// <summary>
    /// Entity name used with the generic FileAttachment API. Received invoices keep their
    /// historical name even though they live in the Invoice table.
    /// </summary>
    public string AttachmentEntityName => IsReceived ? "ReceivedInvoice" : "Invoice";

    // ── Capabilities (what exists for this type at all) ─────────────────────

    public bool SupportsPdf => !IsReceived;
    public bool SupportsEmail => !IsReceived;
    public bool SupportsQr => !IsReceived;
    public bool SupportsReadinessBanner => !IsReceived;

    /// <summary>Create-from-template and save-as-template.</summary>
    public bool SupportsTemplates => !IsReceived;

    public bool ShowsAlertColumn => !IsReceived;
    public bool ShowsEmailSentColumn => !IsReceived;
    public bool UsesNumberSequence => !IsReceived;
    public bool UsesIssuerBankAccountSelect => !IsReceived;
    public bool ValidatesVariableSymbolAsDigits => !IsReceived;

    /// <summary>A proforma is not a tax document, so there is no UBL for it.</summary>
    public bool SupportsUbl => !IsReceived && Type != EDocumentType.Proforma;

    /// <summary>Copying a credit note makes no sense (it must reference its original invoice).</summary>
    public bool SupportsCopy => !IsReceived && Type != EDocumentType.CreditNote;

    public bool ShowsAttachmentsColumn => IsReceived;
    public bool HasReceivedDate => IsReceived;

    /// <summary>The supplier's bank account is typed as free text, not picked from the issuer's accounts.</summary>
    public bool HasFreeTextSupplierBankFields => IsReceived;

    public bool SupportsBulkAttachmentDownload => IsReceived;

    /// <summary>An unissued draft has no legal ISDOC, so bulk ISDOC skips drafts of issued documents.</summary>
    public bool IsdocSkipsDrafts => !IsReceived;

    // ── State machine (which actions the UI offers in which status) ─────────

    public bool CanEdit(EInvoiceStatus status) => IsReceived
        ? status is EInvoiceStatus.Draft or EInvoiceStatus.Completed or EInvoiceStatus.PartiallyPaid
        : status == EInvoiceStatus.Draft;

    public bool CanComplete(EInvoiceStatus status) => status == EInvoiceStatus.Draft;

    public bool CanMarkPaid(EInvoiceStatus status) => IsReceived
        ? status is EInvoiceStatus.Completed or EInvoiceStatus.PartiallyPaid
        : status == EInvoiceStatus.Completed;

    public bool CanMarkUnpaid(EInvoiceStatus status) => IsReceived
        ? status is EInvoiceStatus.Paid or EInvoiceStatus.PartiallyPaid
        : status == EInvoiceStatus.Paid;

    public bool CanRevertToDraft(EInvoiceStatus status) => status == EInvoiceStatus.Completed;

    /// <summary>
    /// Issued: Draft, or Completed (the server still refuses it unless it is the last number).
    /// Received: Draft only.
    /// </summary>
    public bool CanDelete(EInvoiceStatus status) => IsReceived
        ? status == EInvoiceStatus.Draft
        : status is EInvoiceStatus.Draft or EInvoiceStatus.Completed;

    public bool CanRestore(EInvoiceStatus status) => status == EInvoiceStatus.Deleted;

    // ── Accounting export dialog defaults ───────────────────────────────────

    /// <summary>Pre-ticks "include issued" in the accounting export dialog opened from this list.</summary>
    public bool AccountingExportIncludeIssued => !IsReceived;

    /// <summary>Pre-ticks "include received" in the accounting export dialog opened from this list.</summary>
    public bool AccountingExportIncludeReceived => IsReceived;
}

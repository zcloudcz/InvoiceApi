namespace Fakvio.Domain.Enums;

/// <summary>
/// Closed whitelist of files that can be handed out through a short-lived signed download link
/// (<c>POST /api/download-links</c> -> <c>GET /api/download/{token}</c>). Each value maps to exactly one
/// existing controller action; there is deliberately no free-form path or URL anywhere.
/// Existing values must NEVER be renumbered - the integer is stored inside issued tokens.
/// </summary>
public enum EDownloadKind
{
    /// <summary>InvoiceController.ExportToPdf - needs <c>Id</c>, optional <c>TemplateId</c>.</summary>
    InvoicePdf = 1,
    /// <summary>InvoiceController.ExportIsdoc - needs <c>Id</c>.</summary>
    InvoiceIsdoc = 2,
    /// <summary>InvoiceController.ExportUbl - needs <c>Id</c>.</summary>
    InvoiceUbl = 3,
    /// <summary>InvoiceController.BulkExportPdf - needs <c>Ids</c>.</summary>
    InvoiceBulkPdf = 4,
    /// <summary>InvoiceController.BulkExportIsdoc - needs <c>Ids</c>.</summary>
    InvoiceBulkIsdoc = 5,
    /// <summary>InvoiceController.BulkExportUbl - needs <c>Ids</c>.</summary>
    InvoiceBulkUbl = 6,
    /// <summary>ReceivedInvoiceController.ExportIsdoc - needs <c>Id</c>.</summary>
    ReceivedInvoiceIsdoc = 7,
    /// <summary>ReceivedInvoiceController.BulkExportIsdoc - needs <c>Ids</c>.</summary>
    ReceivedInvoiceBulkIsdoc = 8,
    /// <summary>ReceivedInvoiceController.BulkDownloadAttachments - needs <c>Ids</c>.</summary>
    ReceivedInvoiceBulkAttachments = 9,
    /// <summary>FileAttachmentController.Download - needs <c>Id</c> (attachment id).</summary>
    AttachmentFile = 10,
    /// <summary>FileAttachmentController.DownloadAll - needs <c>EntityName</c> and <c>Id</c> (record id).</summary>
    AttachmentsZip = 11,
    /// <summary>AccountingExportController.Export - needs <c>AccountingSystem</c>, <c>From</c>, <c>To</c>.</summary>
    AccountingExport = 12,
    /// <summary>VatReportController.DownloadEpoVatReturn - needs <c>Year</c>, <c>Period</c>, <c>PeriodType</c>.</summary>
    VatEpoReturn = 13,
    /// <summary>VatReportController.DownloadEpoControlStatement - needs <c>Year</c>, <c>Period</c>, <c>PeriodType</c>.</summary>
    VatEpoControlStatement = 14,
    /// <summary>VatReportController.DownloadEpoSummaryStatement - needs <c>Year</c>, <c>Period</c>, <c>PeriodType</c>.</summary>
    VatEpoSummaryStatement = 15
}

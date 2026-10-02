namespace Fakvio.Application.Service;

/// <summary>Outcome of an accounting export: the file plus how many documents made it in or were left out.</summary>
/// <param name="Content">XML bytes.</param>
/// <param name="FileName">Suggested download file name.</param>
/// <param name="ContentType">HTTP content type.</param>
/// <param name="ExportedCount">Documents written to the file.</param>
/// <param name="SkippedDocuments">
/// Document numbers the target system cannot represent correctly (unsupported document type,
/// currency or VAT rate) — deliberately left out instead of exporting wrong data.
/// </param>
public record AccountingExportResult(
    byte[] Content, string FileName, string ContentType, int ExportedCount, IReadOnlyList<string> SkippedDocuments);

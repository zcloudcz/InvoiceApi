using Fakvio.Contracts.Dto.Import;

namespace Fakvio.Application.Service;

/// <summary>
/// Imports clients (contacts) from a CSV export of Fakturoid or iDoklad.
///
/// Two-step workflow, same shape as the PDF invoice import (see <see cref="IInvoiceImportService"/>):
/// 1. Preview: parse the CSV, map columns via header aliases, flag duplicates by IČO. Nothing is saved.
/// 2. Confirm: create the rows the user kept (typically the "New" ones from the preview).
/// </summary>
public interface IClientCsvImportService
{
    /// <summary>
    /// Parses the uploaded CSV and returns one preview row per data row, with a New/Duplicate/Invalid
    /// classification. Duplicate detection uses IČO (registration number), both against the existing
    /// database and against earlier rows in the same file.
    /// </summary>
    /// <param name="csvStream">Raw CSV file stream (encoding/delimiter are auto-detected).</param>
    /// <param name="ct">Cancellation token.</param>
    Task<ClientImportPreviewDto> PreviewAsync(Stream csvStream, CancellationToken ct = default);

    /// <summary>
    /// Creates the confirmed clients. Duplicates are re-checked at this point (in case the database
    /// changed since the preview, or two rows in the request share the same IČO) and skipped rather
    /// than causing the whole import to fail.
    /// </summary>
    /// <param name="request">Clients to create, as reviewed/edited by the user.</param>
    /// <param name="ct">Cancellation token.</param>
    Task<ClientImportResultDto> ConfirmAsync(ClientImportConfirmDto request, CancellationToken ct = default);
}

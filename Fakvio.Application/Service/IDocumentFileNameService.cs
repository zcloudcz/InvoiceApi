namespace Fakvio.Application.Service;

/// <summary>
/// Resolves the file name of a generated document (PDF, ISDOC, UBL) for an invoice.
/// Loads the data and the configured format (client → issuer → system default) and delegates
/// the actual name building to the pure <c>DocumentFileName</c> helper.
/// Every place that hands out a generated document must use this instead of composing names itself.
/// </summary>
public interface IDocumentFileNameService
{
    /// <summary>
    /// File name for one invoice, e.g. "Faktura_2026001.pdf".
    /// </summary>
    /// <param name="invoiceId">Invoice ID.</param>
    /// <param name="extension">Extension with the dot — see the DocumentFileName.*Extension constants.</param>
    /// <exception cref="KeyNotFoundException">The invoice does not exist.</exception>
    Task<string> GetFileNameAsync(long invoiceId, string extension, CancellationToken ct = default);

    /// <summary>
    /// File names for many invoices in a single query (bulk ZIP exports — avoids one query per invoice).
    /// Invoices that do not exist are simply missing from the result.
    /// </summary>
    Task<IReadOnlyDictionary<long, string>> GetFileNamesAsync(
        IReadOnlyCollection<long> invoiceIds, string extension, CancellationToken ct = default);
}

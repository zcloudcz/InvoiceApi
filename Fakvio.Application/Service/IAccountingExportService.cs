using Fakvio.Domain.Enums;

namespace Fakvio.Application.Service;

/// <summary>
/// Orchestrates the "Export do účetnictví" feature: loads issued/received invoices for the
/// requested date range from the tenant database and delegates XML generation to the
/// <see cref="IAccountingExporter"/> registered for the requested <see cref="EAccountingSystem"/>.
/// </summary>
public interface IAccountingExportService
{
    /// <summary>
    /// Generates one XML export file for the given accounting system.
    /// </summary>
    /// <param name="system">Target accounting system (Pohoda / MoneyS3 / AbraFlexi).</param>
    /// <param name="from">Start of the date range (inclusive), matched against IssueDate.</param>
    /// <param name="to">End of the date range (inclusive), matched against IssueDate.</param>
    /// <param name="includeIssued">Include issued invoices/credit notes/proformas.</param>
    /// <param name="includeReceived">Include received (incoming) invoices.</param>
    /// <param name="invoiceIds">
    /// Optional explicit list of issued invoice IDs. When set, overrides the date-range filter
    /// for issued invoices (used for "export selected rows" from the Invoices grid);
    /// received invoices are still filtered by date range.
    /// </param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The generated XML bytes, a suggested file name and the content type to serve it with.</returns>
    /// <exception cref="InvalidOperationException">When the tenant has no issuer company configured.</exception>
    Task<(byte[] Content, string FileName, string ContentType)> ExportAsync(
        EAccountingSystem system,
        DateTime from,
        DateTime to,
        bool includeIssued,
        bool includeReceived,
        IReadOnlyList<long>? invoiceIds = null,
        CancellationToken ct = default);
}

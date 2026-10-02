namespace Fakvio.Contracts.Dto.AccountingExport;

/// <summary>
/// Request body for <c>POST /api/accounting-export/{system}</c> — "Export do účetnictví".
/// The target system itself is the route segment (Pohoda/MoneyS3/AbraFlexi), this DTO only
/// carries the filter.
/// </summary>
public class AccountingExportRequestDto
{
    /// <summary>Start of the date range (inclusive), matched against each document's issue date.</summary>
    public DateTime From { get; set; }

    /// <summary>End of the date range (inclusive), matched against each document's issue date.</summary>
    public DateTime To { get; set; }

    /// <summary>Include issued invoices/credit notes/proformas. Default true.</summary>
    public bool IncludeIssued { get; set; } = true;

    /// <summary>Include received (incoming) invoices. Default true.</summary>
    public bool IncludeReceived { get; set; } = true;

    /// <summary>
    /// Optional explicit list of issued invoice IDs — when set, overrides the <see cref="From"/>/
    /// <see cref="To"/> date filter for issued invoices (used when exporting a selection from the
    /// Invoices grid). Received invoices are always filtered by date range.
    /// </summary>
    public List<long>? InvoiceIds { get; set; }
}

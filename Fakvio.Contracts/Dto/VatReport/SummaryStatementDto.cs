namespace Fakvio.Contracts.Dto.VatReport;

/// <summary>
/// One row of the EU VAT summary statement (DPHSHV, "Souhrnné hlášení") preview:
/// all supplies for one EU customer (country + VAT id) with one supply code.
/// </summary>
public class SummaryStatementRowDto
{
    /// <summary>Two-letter VAT country prefix of the customer (EPO k_stat), e.g. "DE"; Greece is "EL".</summary>
    public string CountryCode { get; set; } = string.Empty;

    /// <summary>Customer VAT id without the country prefix (EPO c_vat).</summary>
    public string VatId { get; set; } = string.Empty;

    /// <summary>Customer name, for display only (not part of the XML).</summary>
    public string? ClientName { get; set; }

    /// <summary>EPO k_pln_eu: 0 = goods, 3 = services (the only two codes Fakvio derives).</summary>
    public int SupplyCode { get; set; }

    /// <summary>Number of invoices aggregated into the row (EPO pln_pocet).</summary>
    public int InvoiceCount { get; set; }

    /// <summary>Total value in whole CZK, rounded up (EPO pln_hodnota).</summary>
    public long TotalCzk { get; set; }
}

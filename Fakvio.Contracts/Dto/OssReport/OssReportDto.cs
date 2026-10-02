namespace Fakvio.Contracts.Dto.OssReport;

/// <summary>
/// Quarterly EU OSS (One-Stop-Shop) report: taxable base and VAT per destination country
/// and VAT rate, always in EUR (the OSS return is filed in EUR whatever the invoice currency).
/// Credit notes reduce the figures. See DEVGUIDE §4.16.
/// </summary>
public class OssReportDto
{
    public int Year { get; set; }

    /// <summary>Calendar quarter 1–4.</summary>
    public int Quarter { get; set; }

    public DateOnly PeriodFrom { get; set; }
    public DateOnly PeriodTo { get; set; }

    /// <summary>Number of OSS documents (invoices + credit notes) in the period.</summary>
    public int DocumentCount { get; set; }

    public List<OssReportLineDto> Lines { get; set; } = new();

    public decimal TotalBaseEur { get; set; }
    public decimal TotalVatEur { get; set; }
}

/// <summary>One row of the OSS return: country + rate → base and VAT in EUR.</summary>
public class OssReportLineDto
{
    /// <summary>ISO 3166-1 alpha-2 destination (consumption) country.</summary>
    public string CountryCode { get; set; } = string.Empty;

    /// <summary>VAT rate of the destination country in percent.</summary>
    public decimal VatRate { get; set; }

    /// <summary>Taxable amount in EUR (negative balance possible when credit notes dominate).</summary>
    public decimal BaseEur { get; set; }

    /// <summary>VAT amount in EUR.</summary>
    public decimal VatEur { get; set; }
}

/// <summary>Result of the OSS destination-country preview for a client (null = not an OSS case).</summary>
public class OssCountryDto
{
    public string? CountryCode { get; set; }
}

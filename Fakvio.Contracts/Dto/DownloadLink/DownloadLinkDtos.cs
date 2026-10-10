using Fakvio.Domain.Enums;

namespace Fakvio.Contracts.Dto.DownloadLink;

/// <summary>
/// Body of <c>POST /api/download-links</c> and, at the same time, the parameter part of the signed token.
/// Which fields are required depends on <see cref="Kind"/> (see <see cref="EDownloadKind"/> member docs);
/// every field not used by the kind is ignored.
/// </summary>
public class CreateDownloadLinkDto
{
    public EDownloadKind Kind { get; set; }

    /// <summary>Single invoice / received invoice / attachment id, or the record id for <see cref="EDownloadKind.AttachmentsZip"/>.</summary>
    public long? Id { get; set; }

    /// <summary>Invoice ids for the bulk kinds (max 100).</summary>
    public List<long>? Ids { get; set; }

    /// <summary>Optional content template for <see cref="EDownloadKind.InvoicePdf"/>.</summary>
    public long? TemplateId { get; set; }

    /// <summary>Entity type of the record whose attachments are zipped, e.g. "Invoice" or "ReceivedInvoice".</summary>
    public string? EntityName { get; set; }

    // Accounting export
    public EAccountingSystem? AccountingSystem { get; set; }
    public DateTime? From { get; set; }
    public DateTime? To { get; set; }
    public bool IncludeIssued { get; set; } = true;
    public bool IncludeReceived { get; set; } = true;

    // VAT EPO exports
    public int? Year { get; set; }
    public int? Period { get; set; }
    public EVatPeriodType? PeriodType { get; set; }

    /// <summary>VAT ids of customers reported as GOODS (EPO return / summary statement only).</summary>
    public List<string>? Goods { get; set; }
}

/// <summary>Response of <c>POST /api/download-links</c>.</summary>
public class DownloadLinkDto
{
    /// <summary>Absolute URL that downloads the file without any Authorization header until <see cref="ExpiresAt"/>.</summary>
    public string Url { get; set; } = string.Empty;

    /// <summary>UTC moment after which the link stops working.</summary>
    public DateTime ExpiresAt { get; set; }
}

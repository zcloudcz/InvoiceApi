using InvoiceApi.Contracts.Common.Pagination;

namespace InvoiceApi.Contracts.Dto.Client;

/// <summary>
/// Filter parameters for clients list
/// </summary>
public class ClientFilterDto : PaginationParams
{
    /// <summary>
    /// Search in company name, trading name, registration number, city
    /// </summary>
    public string? Search { get; set; }

    /// <summary>
    /// Filter by IsVatPayer
    /// </summary>
    public bool? IsVatPayer { get; set; }

    /// <summary>
    /// Filter by IsIssuer
    /// </summary>
    public bool? IsIssuer { get; set; }

    /// <summary>
    /// Include inactive clients
    /// </summary>
    public bool IncludeInactive { get; set; } = false;

    /// <summary>
    /// Filter by city
    /// </summary>
    public string? City { get; set; }

    /// <summary>
    /// Filter by country
    /// </summary>
    public string? Country { get; set; }
}

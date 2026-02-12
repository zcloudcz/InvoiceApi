using InvoiceApi.Application.Dto.Invoice;

namespace InvoiceApi.Application.Dto.Dashboard;

/// <summary>
/// DTO for the main dashboard page. Contains aggregated statistics
/// about invoices, clients, and financial data for the current month.
/// </summary>
public class DashboardDto
{
    /// <summary>
    /// Number of invoices created in the current month.
    /// </summary>
    public int InvoicesThisMonth { get; set; }

    /// <summary>
    /// Total number of active clients (IsActive = true, IsIssuer = false).
    /// </summary>
    public int TotalClients { get; set; }

    /// <summary>
    /// Total amount of unpaid invoices (Completed status, not yet paid).
    /// Includes the currency code for display purposes.
    /// </summary>
    public decimal UnpaidAmount { get; set; }

    /// <summary>
    /// Number of currently active VAT rates.
    /// </summary>
    public int ActiveVatRates { get; set; }

    /// <summary>
    /// Number of invoices that are past their due date but still unpaid.
    /// </summary>
    public int OverdueInvoicesCount { get; set; }

    /// <summary>
    /// List of the 5 most recently created invoices for quick overview.
    /// </summary>
    public List<InvoiceDto> RecentInvoices { get; set; } = new();

    /// <summary>
    /// List of invoices that are past their due date (overdue).
    /// Shows up to 10 overdue invoices sorted by due date ascending (oldest first).
    /// </summary>
    public List<InvoiceDto> OverdueInvoices { get; set; } = new();
}

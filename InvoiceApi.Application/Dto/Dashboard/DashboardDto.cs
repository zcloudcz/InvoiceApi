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

    // ─── Chart Data ──────────────────────────────────────────────────────────

    /// <summary>
    /// Invoice count grouped by status (e.g., {"Draft": 5, "Completed": 12, "Paid": 30}).
    /// Used for the "Invoices by Status" donut chart on the dashboard.
    /// Excludes Deleted status.
    /// </summary>
    public Dictionary<string, int> InvoiceCountByStatus { get; set; } = new();

    /// <summary>
    /// Top 10 clients by total invoice amount (revenue).
    /// Key = client company name, Value = total TotalWithVat amount.
    /// Used for the "Top Clients by Revenue" donut chart.
    /// </summary>
    public Dictionary<string, decimal> InvoiceTotalByClient { get; set; } = new();

    /// <summary>
    /// Top 10 clients by invoice count.
    /// Key = client company name, Value = number of invoices.
    /// Used as a supplementary data point (displayed in tooltip or side table).
    /// </summary>
    public Dictionary<string, int> InvoiceCountByClient { get; set; } = new();
}

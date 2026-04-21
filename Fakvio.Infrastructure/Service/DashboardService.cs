using Fakvio.Contracts.Dto.Dashboard;
using Fakvio.Contracts.Dto.Invoice;
using Fakvio.Application.Service;
using Fakvio.Domain.Entities;
using Fakvio.Domain.Enums;
using Fakvio.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using ZMapper;

namespace Fakvio.Infrastructure.Service;

/// <summary>
/// Dashboard service that aggregates statistics from the database for the home page.
/// Runs multiple efficient queries to gather invoice counts, client counts,
/// unpaid amounts, and recent/overdue invoice lists.
/// </summary>
public class DashboardService : IDashboardService
{
    private readonly TenantDbContext _context;
    private readonly ILogger<DashboardService> _logger;

    public DashboardService(TenantDbContext context, ILogger<DashboardService> logger)
    {
        _context = context;
        _logger = logger;
    }

    /// <summary>
    /// Maps an Invoice entity to InvoiceDto using ZMapper v1.1.0.
    /// ZMapper now handles BaseEntity and nested Ids automatically.
    /// Only navigation-derived properties must be set manually.
    /// </summary>
    private static InvoiceDto MapInvoiceToDto(Invoice entity)
    {
        var dto = entity.ToInvoiceDto();

        // Navigation-derived properties — ZMapper cannot flatten navigation paths
        dto.ClientName = entity.Client?.CompanyName ?? string.Empty;
        dto.IssuerName = entity.Issuer?.CompanyName ?? string.Empty;
        dto.CurrencyCode = entity.Currency?.Code ?? string.Empty;
        dto.CurrencySymbol = entity.Currency?.Symbol ?? string.Empty;
        dto.OriginalInvoiceNumber = entity.OriginalInvoice?.DocumentNumber;

        return dto;
    }

    /// <inheritdoc />
    public async Task<DashboardDto> GetDashboardAsync(long? companyId = null, CancellationToken ct = default)
    {
        _logger.LogInformation("Loading dashboard statistics for companyId: {CompanyId}", companyId);

        var now = DateTime.UtcNow;
        // First day of current month (inclusive) and first day of next month (exclusive).
        // Used to build a half-open [first, nextFirst) window for the "due this month" cashflow aggregation.
        // DateTimeKind.Utc is required — Npgsql rejects Unspecified kind
        // when comparing against PostgreSQL 'timestamp with time zone' columns.
        var firstDayOfMonth = new DateTime(now.Year, now.Month, 1, 0, 0, 0, DateTimeKind.Utc);
        var firstDayOfNextMonth = firstDayOfMonth.AddMonths(1);

        // Base query for invoices — excludes templates (TPH) and soft-deleted invoices.
        // Filtered by company (issuer) if provided.
        // When companyId is null (SysAdmin without impersonation), all invoices are shown.
        // AsNoTracking: entire dashboard is read-only — no entity modifications
        var invoiceQuery = _context.Invoice
            .AsNoTracking()
            .Where(i => !(i is Domain.Entities.InvoiceTemplate)) // Exclude templates (TPH)
            .Where(i => i.Status != EInvoiceStatus.Deleted);      // Exclude soft-deleted

        if (companyId.HasValue)
        {
            invoiceQuery = invoiceQuery.Where(i => i.IssuerId == companyId.Value);
        }

        // Run aggregation queries SEQUENTIALLY — DbContext is NOT thread-safe.
        // Task.WhenAll on the same DbContext instance causes
        // "A second operation was started on this context instance" errors.

        // Aggregate issued invoices whose DueDate falls in the current calendar month.
        // Single GroupBy-style aggregation returns count + both totals in one round-trip.
        // Filter: Completed only (Paid = already collected, not cashflow forecast).
        // DueDate is nullable on the entity — the >= / < window implicitly excludes NULLs.
        var dueThisMonthQuery = invoiceQuery
            .Where(i => i.Status == EInvoiceStatus.Completed
                        && i.DueDate >= firstDayOfMonth
                        && i.DueDate < firstDayOfNextMonth);

        var dueThisMonth = await dueThisMonthQuery
            .GroupBy(_ => 1)
            .Select(g => new
            {
                Count = g.Count(),
                WithoutVat = g.Sum(i => i.TotalBeforeVat),
                WithVat = g.Sum(i => i.TotalWithVat)
            })
            .FirstOrDefaultAsync(ct);

        var invoicesDueThisMonthCount = dueThisMonth?.Count ?? 0;
        var invoicesDueThisMonthTotalWithoutVat = dueThisMonth?.WithoutVat ?? 0m;
        var invoicesDueThisMonthTotalWithVat = dueThisMonth?.WithVat ?? 0m;

        // Count active clients (not issuers, only active)
        var totalClients = await _context.Client
            .CountAsync(c => c.IsActive && !c.IsIssuer, ct);

        // Sum of unpaid invoices (Completed status = issued but not yet paid)
        var unpaidAmount = await invoiceQuery
            .Where(i => i.Status == EInvoiceStatus.Completed)
            .SumAsync(i => (decimal?)i.TotalWithVat ?? 0, ct);

        // Count overdue invoices (Completed, past due date)
        var overdueCount = await invoiceQuery
            .CountAsync(i => i.Status == EInvoiceStatus.Completed && i.DueDate < now, ct);

        // Load the 5 most recent invoices (for "Recent Invoices" table)
        var recentInvoices = await invoiceQuery
            .Include(i => i.Client)
            .Include(i => i.Issuer)
            .Include(i => i.Currency)
            .Include(i => i.InvoiceItem)
            .OrderByDescending(i => i.CreatedAt)
            .Take(5)
            .ToListAsync(ct);

        // Load up to 10 overdue invoices sorted by due date (oldest first)
        var overdueInvoices = await invoiceQuery
            .Where(i => i.Status == EInvoiceStatus.Completed && i.DueDate < now)
            .Include(i => i.Client)
            .Include(i => i.Issuer)
            .Include(i => i.Currency)
            .Include(i => i.InvoiceItem)
            .OrderBy(i => i.DueDate)
            .Take(10)
            .ToListAsync(ct);

        // ─── Chart Data Queries ───────────────────────────────────────────────

        // Invoice count grouped by status (for donut chart).
        // Deleted invoices already excluded by base query.
        var invoiceCountByStatus = await invoiceQuery
            .GroupBy(i => i.Status)
            .Select(g => new { Status = g.Key, Count = g.Count() })
            .ToListAsync(ct);

        // Top 10 clients by total invoice revenue (for donut chart)
        var topClientsByRevenue = await invoiceQuery
            .Where(i => i.Client != null)
            .GroupBy(i => i.Client!.CompanyName)
            .Select(g => new { ClientName = g.Key ?? "Unknown", Total = g.Sum(i => i.TotalWithVat) })
            .OrderByDescending(x => x.Total)
            .Take(10)
            .ToListAsync(ct);

        // Top 10 clients by invoice count
        var topClientsByCount = await invoiceQuery
            .Where(i => i.Client != null)
            .GroupBy(i => i.Client!.CompanyName)
            .Select(g => new { ClientName = g.Key ?? "Unknown", Count = g.Count() })
            .OrderByDescending(x => x.Count)
            .Take(10)
            .ToListAsync(ct);

        // Build the dashboard DTO with all collected data including chart data
        var dashboard = new DashboardDto
        {
            InvoicesDueThisMonthCount = invoicesDueThisMonthCount,
            InvoicesDueThisMonthTotalWithoutVat = invoicesDueThisMonthTotalWithoutVat,
            InvoicesDueThisMonthTotalWithVat = invoicesDueThisMonthTotalWithVat,
            TotalClients = totalClients,
            UnpaidAmount = unpaidAmount,
            OverdueInvoicesCount = overdueCount,
            RecentInvoices = recentInvoices.Select(i => MapInvoiceToDto(i)).ToList(),
            OverdueInvoices = overdueInvoices.Select(i => MapInvoiceToDto(i)).ToList(),
            // Chart data
            InvoiceCountByStatus = invoiceCountByStatus.ToDictionary(x => x.Status.ToString(), x => x.Count),
            InvoiceTotalByClient = topClientsByRevenue.ToDictionary(x => x.ClientName, x => x.Total),
            InvoiceCountByClient = topClientsByCount.ToDictionary(x => x.ClientName, x => x.Count)
        };

        _logger.LogInformation("Dashboard loaded: {DueCount} invoices due this month ({DueTotalWithVat:N2} with VAT), {TotalClients} clients, {UnpaidAmount:N2} unpaid",
            dashboard.InvoicesDueThisMonthCount, dashboard.InvoicesDueThisMonthTotalWithVat, dashboard.TotalClients, dashboard.UnpaidAmount);

        return dashboard;
    }

}

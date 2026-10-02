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

        // ─── New widget series (dashboard-onboarding) ──────────────────────────
        // Shared 12-month window: [firstDayOfMonth - 11 months, firstDayOfNextMonth).
        var seriesStart = firstDayOfMonth.AddMonths(-11);
        var months = Enumerable.Range(0, 12)
            .Select(offset => seriesStart.AddMonths(offset))
            .ToList();

        var revenueByMonth = await BuildRevenueByMonthAsync(invoiceQuery, seriesStart, firstDayOfNextMonth, months, ct);
        var incomeVsExpenseByMonth = await BuildIncomeVsExpenseByMonthAsync(invoiceQuery, companyId, seriesStart, firstDayOfNextMonth, months, ct);
        var receivablesAging = await BuildReceivablesAgingAsync(invoiceQuery, now, ct);

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
            InvoiceCountByClient = topClientsByCount.ToDictionary(x => x.ClientName, x => x.Count),
            RevenueByMonth = revenueByMonth,
            IncomeVsExpenseByMonth = incomeVsExpenseByMonth,
            ReceivablesAging = receivablesAging
        };

        _logger.LogInformation("Dashboard loaded: {DueCount} invoices due this month ({DueTotalWithVat:N2} with VAT), {TotalClients} clients, {UnpaidAmount:N2} unpaid",
            dashboard.InvoicesDueThisMonthCount, dashboard.InvoicesDueThisMonthTotalWithVat, dashboard.TotalClients, dashboard.UnpaidAmount);

        return dashboard;
    }

    /// <summary>
    /// "Tržby po měsících" — one grouped query (Issue date → sums by document type),
    /// then merged in memory onto a fixed 12-month scaffold so months with zero
    /// activity still render as a bar. Credit notes subtract from the invoice total.
    /// </summary>
    private static async Task<List<MonthlyAmountDto>> BuildRevenueByMonthAsync(
        IQueryable<Invoice> invoiceQuery,
        DateTime from,
        DateTime toExclusive,
        List<DateTime> months,
        CancellationToken ct)
    {
        var raw = await invoiceQuery
            .Where(i => (i.DocumentType == EDocumentType.Invoice || i.DocumentType == EDocumentType.CreditNote)
                        && (i.Status == EInvoiceStatus.Completed || i.Status == EInvoiceStatus.Paid || i.Status == EInvoiceStatus.PartiallyPaid)
                        && (i.Currency.Code == "CZK" || i.ExchangeRate != null)
                        && i.IssueDate >= from && i.IssueDate < toExclusive)
            .GroupBy(i => new { i.IssueDate!.Value.Year, i.IssueDate.Value.Month })
            .Select(g => new
            {
                g.Key.Year,
                g.Key.Month,
                InvoiceTotal = g.Where(x => x.DocumentType == EDocumentType.Invoice).Sum(x => (decimal?)(x.TotalBeforeVat * (x.ExchangeRate ?? 1m))) ?? 0m,
                CreditNoteTotal = g.Where(x => x.DocumentType == EDocumentType.CreditNote).Sum(x => (decimal?)(x.TotalBeforeVat * (x.ExchangeRate ?? 1m))) ?? 0m
            })
            .ToListAsync(ct);

        var byKey = raw.ToDictionary(x => (x.Year, x.Month));
        return months.Select(m => new MonthlyAmountDto
        {
            Month = m.ToString("yyyy-MM"),
            Amount = byKey.TryGetValue((m.Year, m.Month), out var v) ? v.InvoiceTotal - v.CreditNoteTotal : 0m
        }).ToList();
    }

    /// <summary>
    /// "Příjmy vs výdaje po měsících" — one grouped query against issued invoices (same
    /// net-of-credit-notes rule as <see cref="BuildRevenueByMonthAsync"/>) and one against
    /// received invoices, merged in memory onto the same 12-month scaffold.
    /// </summary>
    private async Task<List<IncomeExpenseMonthDto>> BuildIncomeVsExpenseByMonthAsync(
        IQueryable<Invoice> invoiceQuery,
        long? companyId,
        DateTime from,
        DateTime toExclusive,
        List<DateTime> months,
        CancellationToken ct)
    {
        var income = await invoiceQuery
            .Where(i => (i.DocumentType == EDocumentType.Invoice || i.DocumentType == EDocumentType.CreditNote)
                        && (i.Status == EInvoiceStatus.Completed || i.Status == EInvoiceStatus.Paid || i.Status == EInvoiceStatus.PartiallyPaid)
                        && (i.Currency.Code == "CZK" || i.ExchangeRate != null)
                        && i.IssueDate >= from && i.IssueDate < toExclusive)
            .GroupBy(i => new { i.IssueDate!.Value.Year, i.IssueDate.Value.Month })
            .Select(g => new
            {
                g.Key.Year,
                g.Key.Month,
                InvoiceTotal = g.Where(x => x.DocumentType == EDocumentType.Invoice).Sum(x => (decimal?)(x.TotalBeforeVat * (x.ExchangeRate ?? 1m))) ?? 0m,
                CreditNoteTotal = g.Where(x => x.DocumentType == EDocumentType.CreditNote).Sum(x => (decimal?)(x.TotalBeforeVat * (x.ExchangeRate ?? 1m))) ?? 0m
            })
            .ToDictionaryAsync(x => (x.Year, x.Month), ct);

        // Received invoices have no Issuer FK (we are always the recipient) — tenant
        // isolation already comes from the schema-per-tenant TenantDbContext, so no
        // companyId filter is needed here (unlike invoiceQuery, which filters by Issuer
        // for multi-issuer tenants). Excludes Rejected/Deleted — not a real expense.
        var expense = await _context.ReceivedInvoice
            .AsNoTracking()
            .Where(r => (r.Status == EReceivedInvoiceStatus.Approved || r.Status == EReceivedInvoiceStatus.Paid)
                        && (r.Currency.Code == "CZK" || r.ExchangeRate != null)
                        && r.IssueDate >= from && r.IssueDate < toExclusive)
            .GroupBy(r => new { r.IssueDate!.Value.Year, r.IssueDate.Value.Month })
            .Select(g => new { g.Key.Year, g.Key.Month, Total = g.Sum(x => x.TotalBeforeVat * (x.ExchangeRate ?? 1m)) })
            .ToDictionaryAsync(x => (x.Year, x.Month), ct);

        return months.Select(m =>
        {
            var key = (m.Year, m.Month);
            income.TryGetValue(key, out var inc);
            expense.TryGetValue(key, out var exp);
            return new IncomeExpenseMonthDto
            {
                Month = m.ToString("yyyy-MM"),
                Income = inc is null ? 0m : inc.InvoiceTotal - inc.CreditNoteTotal,
                Expense = exp?.Total ?? 0m
            };
        }).ToList();
    }

    /// <summary>
    /// "Neuhrazené pohledávky podle stáří" — single query loading the (few) unpaid
    /// issued invoices, bucketed in memory by days since DueDate. Bucketing needs a
    /// CPU-side switch (no clean SQL translation for "days since X, banded"), so the
    /// query only narrows down the rows — the invoice count here is small by definition
    /// (OverdueInvoicesCount is already shown elsewhere on the same dashboard).
    /// </summary>
    private static async Task<ReceivablesAgingDto> BuildReceivablesAgingAsync(
        IQueryable<Invoice> invoiceQuery,
        DateTime now,
        CancellationToken ct)
    {
        var unpaid = await invoiceQuery
            .Where(i => i.DocumentType == EDocumentType.Invoice
                        && (i.Status == EInvoiceStatus.Completed || i.Status == EInvoiceStatus.PartiallyPaid)
                        && (i.Currency.Code == "CZK" || i.ExchangeRate != null))
            .Select(i => new { i.DueDate, i.TotalWithVat, i.PaidAmount, Rate = i.ExchangeRate ?? 1m })
            .ToListAsync(ct);

        var aging = new ReceivablesAgingDto();
        foreach (var inv in unpaid)
        {
            var remaining = (inv.TotalWithVat - inv.PaidAmount) * inv.Rate; // foreign currency: converted by the ČNB rate on the invoice
            if (remaining <= 0) continue;

            // DueDate is nullable on the entity; treat a missing due date as "not yet due"
            // (bucket 0-30) rather than dropping it — an incomplete invoice still owes money.
            var ageDays = inv.DueDate.HasValue ? (now - inv.DueDate.Value).Days : 0;

            if (ageDays <= 30) aging.Bucket0To30 += remaining;
            else if (ageDays <= 60) aging.Bucket31To60 += remaining;
            else if (ageDays <= 90) aging.Bucket61To90 += remaining;
            else aging.BucketOver90 += remaining;
        }

        return aging;
    }
}

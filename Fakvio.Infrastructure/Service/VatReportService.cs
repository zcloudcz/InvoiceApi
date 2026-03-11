using Fakvio.Contracts.Dto.VatReport;
using Fakvio.Application.Service;
using Fakvio.Domain.Enums;
using Fakvio.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Fakvio.Infrastructure.Service;

/// <summary>
/// VAT Report service — aggregates output VAT from issued invoices
/// and input VAT from received invoices for a given period.
/// Uses TaxableSupplyDate (DUZP) as the date criterion because
/// Czech VAT law requires reporting by DUZP, not by issue or payment date.
/// </summary>
public class VatReportService : IVatReportService
{
    private readonly TenantDbContext _context;
    private readonly ILogger<VatReportService> _logger;

    public VatReportService(TenantDbContext context, ILogger<VatReportService> logger)
    {
        _context = context;
        _logger = logger;
    }

    /// <inheritdoc />
    public async Task<VatReportDto> GetReportAsync(DateTime from, DateTime to, CancellationToken ct = default)
    {
        _logger.LogInformation("Generating VAT report for period {From:yyyy-MM-dd} to {To:yyyy-MM-dd}", from, to);

        // Ensure UTC kind for PostgreSQL timestamp comparison
        var fromUtc = DateTime.SpecifyKind(from.Date, DateTimeKind.Utc);
        var toUtc = DateTime.SpecifyKind(to.Date.AddDays(1).AddTicks(-1), DateTimeKind.Utc);

        // ── Output VAT (from issued invoices) ───────────────────────────────
        // Only count Completed, Paid, or Creditnoted invoices (not Draft/Deleted).
        // Filter by TaxableSupplyDate (DUZP) — the legally relevant date for VAT.
        // Exclude InvoiceTemplate entities (TPH discriminator).
        var outputItems = await _context.InvoiceItem
            .AsNoTracking()
            .Where(item => item.Invoice != null
                && !(item.Invoice is Domain.Entities.InvoiceTemplate)
                && item.Invoice.TaxableSupplyDate >= fromUtc
                && item.Invoice.TaxableSupplyDate <= toUtc
                && item.Invoice.Status != EInvoiceStatus.Draft
                && item.Invoice.Status != EInvoiceStatus.Deleted
                && item.Invoice.DocumentType == EDocumentType.Invoice)
            .GroupBy(item => item.VatRatePercentage)
            .Select(g => new
            {
                VatRatePercentage = g.Key,
                BaseAmount = g.Sum(i => i.TotalBeforeVat),
                VatAmount = g.Sum(i => i.VatAmount),
                ItemCount = g.Count()
            })
            .ToListAsync(ct);

        // Count of issued invoices in this period
        var issuedCount = await _context.Invoice
            .AsNoTracking()
            .Where(i => !(i is Domain.Entities.InvoiceTemplate)
                && i.TaxableSupplyDate >= fromUtc
                && i.TaxableSupplyDate <= toUtc
                && i.Status != EInvoiceStatus.Draft
                && i.Status != EInvoiceStatus.Deleted
                && i.DocumentType == EDocumentType.Invoice)
            .CountAsync(ct);

        // Total revenue (before VAT) from issued invoices
        var totalRevenue = await _context.Invoice
            .AsNoTracking()
            .Where(i => !(i is Domain.Entities.InvoiceTemplate)
                && i.TaxableSupplyDate >= fromUtc
                && i.TaxableSupplyDate <= toUtc
                && i.Status != EInvoiceStatus.Draft
                && i.Status != EInvoiceStatus.Deleted
                && i.DocumentType == EDocumentType.Invoice)
            .SumAsync(i => (decimal?)i.TotalBeforeVat ?? 0, ct);

        // ── Input VAT (from received invoices) ──────────────────────────────
        // Only count Approved or Paid received invoices (not Received/Rejected/Deleted).
        // Filter by TaxableSupplyDate (DUZP).
        var inputItems = await _context.ReceivedInvoiceItem
            .AsNoTracking()
            .Where(item => item.ReceivedInvoice != null
                && item.ReceivedInvoice.TaxableSupplyDate >= fromUtc
                && item.ReceivedInvoice.TaxableSupplyDate <= toUtc
                && item.ReceivedInvoice.Status != EReceivedInvoiceStatus.Received
                && item.ReceivedInvoice.Status != EReceivedInvoiceStatus.Rejected
                && item.ReceivedInvoice.Status != EReceivedInvoiceStatus.Deleted)
            .GroupBy(item => item.VatRatePercentage)
            .Select(g => new
            {
                VatRatePercentage = g.Key,
                BaseAmount = g.Sum(i => i.TotalBeforeVat),
                VatAmount = g.Sum(i => i.VatAmount),
                ItemCount = g.Count()
            })
            .ToListAsync(ct);

        // Count of received invoices in this period
        var receivedCount = await _context.ReceivedInvoice
            .AsNoTracking()
            .Where(r => r.TaxableSupplyDate >= fromUtc
                && r.TaxableSupplyDate <= toUtc
                && r.Status != EReceivedInvoiceStatus.Received
                && r.Status != EReceivedInvoiceStatus.Rejected
                && r.Status != EReceivedInvoiceStatus.Deleted)
            .CountAsync(ct);

        // Total expenses (before VAT) from received invoices
        var totalExpenses = await _context.ReceivedInvoice
            .AsNoTracking()
            .Where(r => r.TaxableSupplyDate >= fromUtc
                && r.TaxableSupplyDate <= toUtc
                && r.Status != EReceivedInvoiceStatus.Received
                && r.Status != EReceivedInvoiceStatus.Rejected
                && r.Status != EReceivedInvoiceStatus.Deleted)
            .SumAsync(r => (decimal?)r.TotalBeforeVat ?? 0, ct);

        // ── Build report ─────────────────────────────────────────────────────
        var totalOutputVat = outputItems.Sum(x => x.VatAmount);
        var totalInputVat = inputItems.Sum(x => x.VatAmount);

        var report = new VatReportDto
        {
            PeriodFrom = from,
            PeriodTo = to,
            OutputVat = outputItems.Select(x => new VatReportLineDto
            {
                VatRatePercentage = x.VatRatePercentage,
                VatRateLabel = $"DPH {x.VatRatePercentage}%",
                BaseAmount = x.BaseAmount,
                VatAmount = x.VatAmount,
                ItemCount = x.ItemCount
            }).OrderByDescending(x => x.VatRatePercentage).ToList(),

            InputVat = inputItems.Select(x => new VatReportLineDto
            {
                VatRatePercentage = x.VatRatePercentage,
                VatRateLabel = $"DPH {x.VatRatePercentage}%",
                BaseAmount = x.BaseAmount,
                VatAmount = x.VatAmount,
                ItemCount = x.ItemCount
            }).OrderByDescending(x => x.VatRatePercentage).ToList(),

            TotalOutputVat = totalOutputVat,
            TotalInputVat = totalInputVat,
            TaxLiability = totalOutputVat - totalInputVat,
            TotalRevenue = totalRevenue,
            TotalExpenses = totalExpenses,
            Profit = totalRevenue - totalExpenses,
            IssuedInvoiceCount = issuedCount,
            ReceivedInvoiceCount = receivedCount
        };

        _logger.LogInformation(
            "VAT report generated: Output={OutputVat:N2}, Input={InputVat:N2}, Liability={Liability:N2}",
            report.TotalOutputVat, report.TotalInputVat, report.TaxLiability);

        return report;
    }
}

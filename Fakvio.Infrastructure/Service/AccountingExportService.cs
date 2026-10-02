using Fakvio.Application.Service;
using Fakvio.Domain.Enums;
using Fakvio.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Fakvio.Infrastructure.Service;

/// <summary>
/// Loads issued/received invoices for the requested date range from the tenant database and
/// delegates XML generation to the <see cref="IAccountingExporter"/> registered for the
/// requested system. One <see cref="IAccountingExporter"/> per <see cref="EAccountingSystem"/>
/// is resolved from DI by matching its <c>System</c> property (TODO.md §"Export do účetnictví").
/// </summary>
public class AccountingExportService : IAccountingExportService
{
    private readonly TenantDbContext _db;
    private readonly IEnumerable<IAccountingExporter> _exporters;
    private readonly ILogger<AccountingExportService> _logger;

    public AccountingExportService(
        TenantDbContext db, IEnumerable<IAccountingExporter> exporters, ILogger<AccountingExportService> logger)
    {
        _db = db;
        _exporters = exporters;
        _logger = logger;
    }

    /// <summary>Longest allowed export period (days) — keeps a single request bounded.</summary>
    internal const int MaxRangeDays = 366;

    /// <summary>Most explicit invoice ids accepted in one request.</summary>
    internal const int MaxInvoiceIds = 5000;

    public async Task<AccountingExportResult> ExportAsync(
        EAccountingSystem system, DateTime from, DateTime to,
        bool includeIssued, bool includeReceived,
        IReadOnlyList<long>? invoiceIds = null, CancellationToken ct = default)
    {
        if (from.Date > to.Date)
            throw new ArgumentException("The start date must not be after the end date.");
        if ((to.Date - from.Date).TotalDays > MaxRangeDays)
            throw new ArgumentException($"The export period must not exceed {MaxRangeDays} days.");
        if (invoiceIds is { Count: > MaxInvoiceIds })
            throw new ArgumentException($"At most {MaxInvoiceIds} invoices can be exported at once.");

        var exporter = _exporters.FirstOrDefault(e => e.System == system)
            ?? throw new InvalidOperationException($"No accounting exporter registered for system '{system}'.");

        // Date range is inclusive on both ends — "to" is widened to end-of-day so a user picking
        // the same day for from/to gets that whole day's invoices.
        var fromDate = from.Date;
        var toDate = to.Date.AddDays(1).AddTicks(-1);

        var issuer = await _db.Client.AsNoTracking().FirstOrDefaultAsync(c => c.IsIssuer, ct)
            ?? throw new InvalidOperationException("Tenant has no issuer company (Client with IsIssuer = true) configured.");

        var issuedInvoices = new List<Domain.Entities.Invoice>();
        if (includeIssued)
        {
            var query = _db.Invoice
                .AsNoTracking()
                .AsSplitQuery()
                .Where(i => i.Status != EInvoiceStatus.Draft && i.Status != EInvoiceStatus.Deleted);

            query = invoiceIds is { Count: > 0 }
                ? query.Where(i => invoiceIds.Contains(i.Id))
                : query.Where(i => i.IssueDate != null && i.IssueDate >= fromDate && i.IssueDate <= toDate);

            issuedInvoices = await query
                .Include(i => i.Currency)
                .Include(i => i.InvoiceItem)
                .Include(i => i.Client).ThenInclude(c => c!.Address)
                .OrderBy(i => i.IssueDate)
                .ToListAsync(ct);
        }

        var receivedInvoices = new List<Domain.Entities.ReceivedInvoice>();
        if (includeReceived)
        {
            receivedInvoices = await _db.ReceivedInvoice
                .AsNoTracking()
                .AsSplitQuery()
                .Where(i => i.Status != EReceivedInvoiceStatus.Deleted && i.Status != EReceivedInvoiceStatus.Rejected)
                .Where(i => i.IssueDate != null && i.IssueDate >= fromDate && i.IssueDate <= toDate)
                .Include(i => i.Currency)
                .Include(i => i.Items)
                .Include(i => i.Supplier).ThenInclude(c => c.Address)
                .OrderBy(i => i.IssueDate)
                .ToListAsync(ct);
        }

        _logger.LogInformation(
            "Accounting export ({System}) {From:yyyy-MM-dd}..{To:yyyy-MM-dd}: {Issued} issued, {Received} received",
            system, fromDate, toDate, issuedInvoices.Count, receivedInvoices.Count);

        // Leave out documents the target system cannot represent correctly (see IAccountingExporter.CanExport).
        var skipped = issuedInvoices.Where(i => !exporter.CanExport(i)).Select(i => i.DocumentNumber ?? $"#{i.Id}")
            .Concat(receivedInvoices.Where(i => !exporter.CanExport(i)).Select(i => i.DocumentNumber ?? $"#{i.Id}"))
            .ToList();
        var exportable = issuedInvoices.Where(exporter.CanExport).ToList();
        var exportableReceived = receivedInvoices.Where(exporter.CanExport).ToList();
        if (skipped.Count > 0)
            _logger.LogWarning("Accounting export ({System}) skipped {Count} unsupported document(s)", system, skipped.Count);

        var content = exporter.Export(exportable, exportableReceived, issuer);
        var fileName = $"{system}_{fromDate:yyyyMMdd}-{toDate:yyyyMMdd}.{exporter.FileExtension}";
        return new AccountingExportResult(content, fileName, exporter.ContentType,
            exportable.Count + exportableReceived.Count, skipped);
    }
}

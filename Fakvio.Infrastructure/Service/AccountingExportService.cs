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

    public async Task<(byte[] Content, string FileName, string ContentType)> ExportAsync(
        EAccountingSystem system, DateTime from, DateTime to,
        bool includeIssued, bool includeReceived,
        IReadOnlyList<long>? invoiceIds = null, CancellationToken ct = default)
    {
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

        var content = exporter.Export(issuedInvoices, receivedInvoices, issuer);
        var fileName = $"{system}_{fromDate:yyyyMMdd}-{toDate:yyyyMMdd}.{exporter.FileExtension}";
        return (content, fileName, exporter.ContentType);
    }
}

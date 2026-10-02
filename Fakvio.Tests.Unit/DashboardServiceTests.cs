using Fakvio.Domain.Entities;
using Fakvio.Domain.Enums;
using Fakvio.Infrastructure.Data;
using Fakvio.Infrastructure.Service;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using NSubstitute;
using Shouldly;

namespace Fakvio.Tests.Unit;

/// <summary>
/// Unit tests for DashboardService.
/// Tests the aggregation logic for dashboard statistics:
/// invoice counts, client counts, unpaid totals, overdue invoices, etc.
/// </summary>
public class DashboardServiceTests : IDisposable
{
    private readonly TenantDbContext _context;
    private readonly DashboardService _service;
    private readonly ILogger<DashboardService> _logger;

    public DashboardServiceTests()
    {
        // Fresh in-memory database for each test
        var options = new DbContextOptionsBuilder<TenantDbContext>()
            .UseInMemoryDatabase(databaseName: Guid.NewGuid().ToString())
            .Options;

        _context = new TenantDbContext(options);
        _logger = Substitute.For<ILogger<DashboardService>>();
        _service = new DashboardService(_context, _logger);
    }

    public void Dispose()
    {
        _context.Database.EnsureDeleted();
        _context.Dispose();
    }

    /// <summary>
    /// Helper to add a client to the in-memory database.
    /// </summary>
    private Client AddClient(string name, bool isIssuer = false, bool isActive = true)
    {
        var client = new Client
        {
            CompanyName = name,
            RegistrationNumber = Guid.NewGuid().ToString()[..8],
            IsIssuer = isIssuer,
            IsActive = isActive
        };
        _context.Client.Add(client);
        _context.SaveChanges();
        return client;
    }

    /// <summary>
    /// Helper to add an invoice to the in-memory database.
    /// </summary>
    private Invoice AddInvoice(long clientId, long issuerId, EInvoiceStatus status,
        decimal totalWithVat = 1000, DateTime? dueDate = null)
    {
        var currency = _context.Currency.FirstOrDefault();
        if (currency == null)
        {
            currency = new Currency { Code = "CZK", Symbol = "Kč", Name = "Czech Koruna", IsActive = true };
            _context.Currency.Add(currency);
            _context.SaveChanges();
        }

        var invoice = new Invoice
        {
            DocumentType = EDocumentType.Invoice,
            Status = status,
            DocumentNumber = $"INV{Guid.NewGuid().ToString()[..6]}",
            IssueDate = DateTime.UtcNow,
            DueDate = dueDate ?? DateTime.UtcNow.AddDays(14),
            ClientId = clientId,
            IssuerId = issuerId,
            CurrencyId = currency.Id,
            TotalWithVat = totalWithVat,
            TotalBeforeVat = totalWithVat * 0.8264m,
            TotalVat = totalWithVat * 0.1736m,
            InvoiceItem = new List<InvoiceItem>()
        };
        _context.Invoice.Add(invoice);
        _context.SaveChanges();
        return invoice;
    }

    /// <summary>
    /// Tests that an empty database returns all zero values on the dashboard.
    /// </summary>
    [Fact]
    public async Task GetDashboardAsync_EmptyDatabase_ReturnsZeros()
    {
        // Act
        var result = await _service.GetDashboardAsync();

        // Assert - everything should be zero/empty
        result.InvoicesDueThisMonthCount.ShouldBe(0);
        result.InvoicesDueThisMonthTotalWithoutVat.ShouldBe(0);
        result.InvoicesDueThisMonthTotalWithVat.ShouldBe(0);
        result.TotalClients.ShouldBe(0);
        result.UnpaidAmount.ShouldBe(0);
        result.OverdueInvoicesCount.ShouldBe(0);
        result.RecentInvoices.ShouldBeEmpty();
        result.OverdueInvoices.ShouldBeEmpty();
    }

    /// <summary>
    /// Tests that TotalClients counts only active non-issuer clients.
    /// Issuers and inactive clients should not be counted.
    /// </summary>
    [Fact]
    public async Task GetDashboardAsync_CountsOnlyActiveNonIssuerClients()
    {
        // Arrange
        AddClient("Active Client 1");          // should count
        AddClient("Active Client 2");          // should count
        AddClient("Inactive Client", isActive: false); // should NOT count
        AddClient("Issuer Company", isIssuer: true);   // should NOT count

        // Act
        var result = await _service.GetDashboardAsync();

        // Assert - only 2 active non-issuer clients
        result.TotalClients.ShouldBe(2);
    }

    /// <summary>
    /// Tests that UnpaidAmount sums only Completed (not Paid) invoices.
    /// Draft and Paid invoices should not be included.
    /// </summary>
    [Fact]
    public async Task GetDashboardAsync_SumsOnlyCompletedInvoiceAmounts()
    {
        // Arrange
        var issuer = AddClient("Issuer", isIssuer: true);
        var client = AddClient("Client");

        AddInvoice(client.Id, issuer.Id, EInvoiceStatus.Completed, 5000);  // unpaid
        AddInvoice(client.Id, issuer.Id, EInvoiceStatus.Completed, 3000);  // unpaid
        AddInvoice(client.Id, issuer.Id, EInvoiceStatus.Draft, 2000);      // not counted (draft)
        AddInvoice(client.Id, issuer.Id, EInvoiceStatus.Paid, 1000);       // not counted (paid)

        // Act
        var result = await _service.GetDashboardAsync();

        // Assert - only the two Completed invoices count
        result.UnpaidAmount.ShouldBe(8000);
    }

    /// <summary>
    /// Tests that overdue invoices are correctly identified:
    /// Completed status + DueDate in the past.
    /// </summary>
    [Fact]
    public async Task GetDashboardAsync_IdentifiesOverdueInvoices()
    {
        // Arrange
        var issuer = AddClient("Issuer", isIssuer: true);
        var client = AddClient("Client");

        // Overdue: Completed + past due date
        AddInvoice(client.Id, issuer.Id, EInvoiceStatus.Completed, 1000, DateTime.UtcNow.AddDays(-10));
        // Not overdue: Completed + future due date
        AddInvoice(client.Id, issuer.Id, EInvoiceStatus.Completed, 2000, DateTime.UtcNow.AddDays(10));
        // Not overdue: Paid (even if past due date)
        AddInvoice(client.Id, issuer.Id, EInvoiceStatus.Paid, 3000, DateTime.UtcNow.AddDays(-5));

        // Act
        var result = await _service.GetDashboardAsync();

        // Assert - only 1 overdue invoice
        result.OverdueInvoicesCount.ShouldBe(1);
        result.OverdueInvoices.Count.ShouldBe(1);
        result.OverdueInvoices.First().TotalWithVat.ShouldBe(1000);
    }

    /// <summary>
    /// Tests that RecentInvoices returns at most 5 invoices, ordered by creation date.
    /// </summary>
    [Fact]
    public async Task GetDashboardAsync_ReturnsMaxFiveRecentInvoices()
    {
        // Arrange
        var issuer = AddClient("Issuer", isIssuer: true);
        var client = AddClient("Client");

        // Add 7 invoices
        for (int i = 0; i < 7; i++)
        {
            AddInvoice(client.Id, issuer.Id, EInvoiceStatus.Draft, 1000 + i * 100);
        }

        // Act
        var result = await _service.GetDashboardAsync();

        // Assert - should return at most 5 recent invoices
        result.RecentInvoices.Count.ShouldBeLessThanOrEqualTo(5);
    }

    /// <summary>
    /// Tests the "invoices due this month" cashflow aggregation:
    /// - Only Completed invoices are counted (Draft/Paid/Creditnoted/Deleted excluded).
    /// - Only DueDate inside the current calendar month window is counted.
    /// - Totals (without/with VAT) sum across the matching invoices.
    /// </summary>
    [Fact]
    public async Task GetDashboardAsync_InvoicesDueThisMonth_AggregatesCorrectly()
    {
        // Arrange — invoices with due dates inside and outside the current month
        var issuer = AddClient("Issuer", isIssuer: true);
        var client = AddClient("Client");

        var now = DateTime.UtcNow;
        var midThisMonth = new DateTime(now.Year, now.Month, 15, 0, 0, 0, DateTimeKind.Utc);
        var endOfThisMonth = new DateTime(now.Year, now.Month, DateTime.DaysInMonth(now.Year, now.Month), 0, 0, 0, DateTimeKind.Utc);
        var nextMonth = new DateTime(now.Year, now.Month, 1, 0, 0, 0, DateTimeKind.Utc).AddMonths(1).AddDays(5);
        var previousMonth = new DateTime(now.Year, now.Month, 1, 0, 0, 0, DateTimeKind.Utc).AddDays(-5);

        // Two Completed invoices with DueDate in the current month → both counted
        AddInvoice(client.Id, issuer.Id, EInvoiceStatus.Completed, 10_000, midThisMonth);
        AddInvoice(client.Id, issuer.Id, EInvoiceStatus.Completed, 5_000, endOfThisMonth);
        // Draft in the current month → excluded (not yet issued)
        AddInvoice(client.Id, issuer.Id, EInvoiceStatus.Draft, 2_000, midThisMonth);
        // Paid in the current month → excluded (already collected)
        AddInvoice(client.Id, issuer.Id, EInvoiceStatus.Paid, 3_000, midThisMonth);
        // Completed but due next month → excluded (out of window)
        AddInvoice(client.Id, issuer.Id, EInvoiceStatus.Completed, 7_000, nextMonth);
        // Completed but due last month → excluded (out of window, also overdue bucket)
        AddInvoice(client.Id, issuer.Id, EInvoiceStatus.Completed, 4_000, previousMonth);

        // Act
        var result = await _service.GetDashboardAsync();

        // Assert — only the two matching invoices are aggregated
        result.InvoicesDueThisMonthCount.ShouldBe(2);
        // AddInvoice stores TotalWithVat and derives TotalBeforeVat = TotalWithVat * 0.8264m
        result.InvoicesDueThisMonthTotalWithVat.ShouldBe(15_000m);
        result.InvoicesDueThisMonthTotalWithoutVat.ShouldBe(15_000m * 0.8264m);
    }

    // ─── Phase D: Chart Data Tests ───────────────────────────────────────────

    /// <summary>
    /// Tests that InvoiceCountByStatus groups invoices correctly by status.
    /// Deleted invoices are excluded from the grouping; Draft, Completed, and Paid
    /// each appear as separate dictionary entries with their respective counts.
    /// </summary>
    [Fact]
    public async Task GetDashboardAsync_InvoiceCountByStatus_GroupsCorrectly()
    {
        // Arrange — create invoices in different statuses
        var issuer = AddClient("Issuer", isIssuer: true);
        var client = AddClient("Client");

        AddInvoice(client.Id, issuer.Id, EInvoiceStatus.Draft, 1000);
        AddInvoice(client.Id, issuer.Id, EInvoiceStatus.Draft, 2000);
        AddInvoice(client.Id, issuer.Id, EInvoiceStatus.Completed, 3000);
        AddInvoice(client.Id, issuer.Id, EInvoiceStatus.Paid, 4000);
        AddInvoice(client.Id, issuer.Id, EInvoiceStatus.Deleted, 5000); // excluded

        // Act
        var result = await _service.GetDashboardAsync();

        // Assert — Deleted should NOT appear, others grouped correctly
        result.InvoiceCountByStatus.ShouldNotContainKey("Deleted");
        result.InvoiceCountByStatus["Draft"].ShouldBe(2);
        result.InvoiceCountByStatus["Completed"].ShouldBe(1);
        result.InvoiceCountByStatus["Paid"].ShouldBe(1);
    }

    /// <summary>
    /// Tests that InvoiceTotalByClient returns top clients by revenue.
    /// The dictionary keys are client company names; values are the sum of TotalWithVat
    /// across all non-deleted invoices for each client.
    /// </summary>
    [Fact]
    public async Task GetDashboardAsync_TopClientsByRevenue_AggregatesCorrectly()
    {
        // Arrange — two clients with different invoice totals
        var issuer = AddClient("Issuer", isIssuer: true);
        var clientA = AddClient("Alpha Corp");
        var clientB = AddClient("Beta Ltd");

        // Alpha: 2 invoices totaling 8000
        AddInvoice(clientA.Id, issuer.Id, EInvoiceStatus.Completed, 5000);
        AddInvoice(clientA.Id, issuer.Id, EInvoiceStatus.Paid, 3000);
        // Beta: 1 invoice totaling 12000
        AddInvoice(clientB.Id, issuer.Id, EInvoiceStatus.Completed, 12000);
        // Deleted invoice should NOT be included
        AddInvoice(clientA.Id, issuer.Id, EInvoiceStatus.Deleted, 99000);

        // Act
        var result = await _service.GetDashboardAsync();

        // Assert — both clients present with correct totals
        result.InvoiceTotalByClient.ShouldContainKey("Alpha Corp");
        result.InvoiceTotalByClient.ShouldContainKey("Beta Ltd");
        result.InvoiceTotalByClient["Alpha Corp"].ShouldBe(8000);
        result.InvoiceTotalByClient["Beta Ltd"].ShouldBe(12000);
    }

    /// <summary>
    /// Tests that chart data returns empty dictionaries for an empty database.
    /// This ensures the dashboard doesn't throw when there are no invoices to chart.
    /// </summary>
    [Fact]
    public async Task GetDashboardAsync_EmptyDatabase_ReturnsEmptyChartData()
    {
        // Act — no invoices in the database
        var result = await _service.GetDashboardAsync();

        // Assert — chart dictionaries exist but are empty
        result.InvoiceCountByStatus.ShouldNotBeNull();
        result.InvoiceCountByStatus.ShouldBeEmpty();
        result.InvoiceTotalByClient.ShouldNotBeNull();
        result.InvoiceTotalByClient.ShouldBeEmpty();
        result.InvoiceCountByClient.ShouldNotBeNull();
        result.InvoiceCountByClient.ShouldBeEmpty();
    }

    // ─── New widget series (dashboard-onboarding) ──────────────────────────────

    /// <summary>
    /// Helper mirroring <see cref="AddInvoice"/> but with the extra knobs the new series
    /// need: explicit document type, issue date and paid amount.
    /// </summary>
    private Invoice AddInvoiceFull(long clientId, long issuerId, EInvoiceStatus status,
        EDocumentType documentType, DateTime issueDate, decimal totalWithVat = 1000,
        decimal paidAmount = 0, DateTime? dueDate = null)
    {
        var currency = _context.Currency.FirstOrDefault();
        if (currency == null)
        {
            currency = new Currency { Code = "CZK", Symbol = "Kč", Name = "Czech Koruna", IsActive = true };
            _context.Currency.Add(currency);
            _context.SaveChanges();
        }

        var invoice = new Invoice
        {
            DocumentType = documentType,
            Status = status,
            DocumentNumber = $"INV{Guid.NewGuid().ToString()[..6]}",
            IssueDate = issueDate,
            DueDate = dueDate ?? issueDate.AddDays(14),
            ClientId = clientId,
            IssuerId = issuerId,
            CurrencyId = currency.Id,
            TotalWithVat = totalWithVat,
            TotalBeforeVat = totalWithVat * 0.8264m,
            TotalVat = totalWithVat * 0.1736m,
            PaidAmount = paidAmount,
            InvoiceItem = new List<InvoiceItem>()
        };
        _context.Invoice.Add(invoice);
        _context.SaveChanges();
        return invoice;
    }

    private ReceivedInvoice AddReceivedInvoice(long supplierId, EReceivedInvoiceStatus status,
        DateTime issueDate, decimal totalBeforeVat)
    {
        var received = new ReceivedInvoice
        {
            Status = status,
            SupplierId = supplierId,
            CurrencyId = _context.Currency.First(c => c.Code == "CZK").Id,
            IssueDate = issueDate,
            TotalBeforeVat = totalBeforeVat,
            TotalWithVat = totalBeforeVat * 1.21m
        };
        _context.ReceivedInvoice.Add(received);
        _context.SaveChanges();
        return received;
    }

    /// <summary>
    /// RevenueByMonth always has exactly 12 entries, oldest first, even with no data.
    /// </summary>
    [Fact]
    public async Task GetDashboardAsync_RevenueByMonth_EmptyDatabase_Returns12ZeroMonths()
    {
        var result = await _service.GetDashboardAsync();

        result.RevenueByMonth.Count.ShouldBe(12);
        result.RevenueByMonth.ShouldAllBe(m => m.Amount == 0);
        // Oldest first: the last entry is the current month.
        result.RevenueByMonth[^1].Month.ShouldBe(DateTime.UtcNow.ToString("yyyy-MM"));
    }

    /// <summary>
    /// A credit note in the same month as an invoice subtracts from that month's revenue
    /// (net-of-credit-notes rule), while documents outside Completed/Paid/PartiallyPaid
    /// (e.g. Draft) are ignored.
    /// </summary>
    [Fact]
    public async Task GetDashboardAsync_RevenueByMonth_SubtractsCreditNotesFromSameMonth()
    {
        var issuer = AddClient("Issuer", isIssuer: true);
        var client = AddClient("Client");
        var thisMonth = new DateTime(DateTime.UtcNow.Year, DateTime.UtcNow.Month, 15, 0, 0, 0, DateTimeKind.Utc);

        AddInvoiceFull(client.Id, issuer.Id, EInvoiceStatus.Completed, EDocumentType.Invoice, thisMonth, totalWithVat: 1210); // TotalBeforeVat ~999.9
        AddInvoiceFull(client.Id, issuer.Id, EInvoiceStatus.Completed, EDocumentType.CreditNote, thisMonth, totalWithVat: 242); // ~200.0 subtracted
        AddInvoiceFull(client.Id, issuer.Id, EInvoiceStatus.Draft, EDocumentType.Invoice, thisMonth, totalWithVat: 5000); // ignored (Draft)

        var result = await _service.GetDashboardAsync();

        var currentMonth = result.RevenueByMonth[^1];
        currentMonth.Amount.ShouldBe(999.944m - 199.9888m, tolerance: 0.01m);
    }

    /// <summary>
    /// IncomeVsExpenseByMonth pairs issued-invoice income with received-invoice expense
    /// for the same month; a Rejected received invoice is excluded.
    /// </summary>
    [Fact]
    public async Task GetDashboardAsync_IncomeVsExpenseByMonth_PairsIssuedAndReceivedTotals()
    {
        var issuer = AddClient("Issuer", isIssuer: true);
        var client = AddClient("Client");
        var supplier = AddClient("Supplier");
        var thisMonth = new DateTime(DateTime.UtcNow.Year, DateTime.UtcNow.Month, 10, 0, 0, 0, DateTimeKind.Utc);

        AddInvoiceFull(client.Id, issuer.Id, EInvoiceStatus.Paid, EDocumentType.Invoice, thisMonth, totalWithVat: 1210);
        AddReceivedInvoice(supplier.Id, EReceivedInvoiceStatus.Approved, thisMonth, totalBeforeVat: 300);
        AddReceivedInvoice(supplier.Id, EReceivedInvoiceStatus.Rejected, thisMonth, totalBeforeVat: 9999); // excluded

        var result = await _service.GetDashboardAsync();

        var currentMonth = result.IncomeVsExpenseByMonth[^1];
        currentMonth.Income.ShouldBe(999.944m, tolerance: 0.01m);
        currentMonth.Expense.ShouldBe(300m);
    }

    /// <summary>
    /// ReceivablesAging buckets the remaining (TotalWithVat - PaidAmount) by days since
    /// DueDate, and skips invoices that are already fully paid off (remaining &lt;= 0).
    /// </summary>
    [Fact]
    public async Task GetDashboardAsync_ReceivablesAging_BucketsByDaysSinceDueDate()
    {
        var issuer = AddClient("Issuer", isIssuer: true);
        var client = AddClient("Client");
        var now = DateTime.UtcNow;

        // 0-30: due in 10 days (not yet due)
        AddInvoiceFull(client.Id, issuer.Id, EInvoiceStatus.Completed, EDocumentType.Invoice, now.AddDays(-5), totalWithVat: 1000, dueDate: now.AddDays(10));
        // 31-60: 45 days overdue
        AddInvoiceFull(client.Id, issuer.Id, EInvoiceStatus.Completed, EDocumentType.Invoice, now.AddDays(-50), totalWithVat: 2000, dueDate: now.AddDays(-45));
        // 61-90: 70 days overdue, partially paid — only the remainder counts
        AddInvoiceFull(client.Id, issuer.Id, EInvoiceStatus.PartiallyPaid, EDocumentType.Invoice, now.AddDays(-75), totalWithVat: 3000, paidAmount: 1000, dueDate: now.AddDays(-70));
        // 90+: 120 days overdue
        AddInvoiceFull(client.Id, issuer.Id, EInvoiceStatus.Completed, EDocumentType.Invoice, now.AddDays(-125), totalWithVat: 4000, dueDate: now.AddDays(-120));
        // Fully paid off (remaining <= 0) — must not appear anywhere despite being overdue
        AddInvoiceFull(client.Id, issuer.Id, EInvoiceStatus.PartiallyPaid, EDocumentType.Invoice, now.AddDays(-100), totalWithVat: 500, paidAmount: 500, dueDate: now.AddDays(-95));

        var result = await _service.GetDashboardAsync();

        result.ReceivablesAging.Bucket0To30.ShouldBe(1000);
        result.ReceivablesAging.Bucket31To60.ShouldBe(2000);
        result.ReceivablesAging.Bucket61To90.ShouldBe(2000); // 3000 - 1000 paid
        result.ReceivablesAging.BucketOver90.ShouldBe(4000);
    }

    /// <summary>
    /// Foreign-currency invoices count in CZK when they carry a ČNB rate (Invoice.ExchangeRate, CZK per 1 unit);
    /// one without a rate stays out of the CZK series rather than being added 1:1.
    /// </summary>
    [Fact]
    public async Task GetDashboardAsync_ForeignInvoices_AreConvertedByStoredRate_AndRateLessOnesAreLeftOut()
    {
        var issuer = AddClient("Issuer", isIssuer: true);
        var client = AddClient("Client");
        var thisMonth = new DateTime(DateTime.UtcNow.Year, DateTime.UtcNow.Month, 15, 0, 0, 0, DateTimeKind.Utc);
        var eur = new Currency { Code = "EUR", Symbol = "EUR", Name = "Euro", IsActive = true };
        _context.Currency.Add(eur);
        _context.SaveChanges();

        void AddEur(decimal totalWithVat, decimal? rate)
        {
            _context.Invoice.Add(new Invoice
            {
                DocumentType = EDocumentType.Invoice, Status = EInvoiceStatus.Completed,
                DocumentNumber = $"E{Guid.NewGuid().ToString()[..6]}", IssueDate = thisMonth, DueDate = thisMonth.AddDays(14),
                ClientId = client.Id, IssuerId = issuer.Id, CurrencyId = eur.Id,
                TotalWithVat = totalWithVat, TotalBeforeVat = totalWithVat, ExchangeRate = rate,
                InvoiceItem = new List<InvoiceItem>()
            });
            _context.SaveChanges();
        }

        AddEur(100m, 24m);    // 2 400 CZK
        AddEur(5000m, null);  // no rate: excluded

        var result = await _service.GetDashboardAsync();

        result.RevenueByMonth[^1].Amount.ShouldBe(2400m);
        result.IncomeVsExpenseByMonth[^1].Income.ShouldBe(2400m);
        result.ReceivablesAging.Bucket0To30.ShouldBe(2400m);
    }
}

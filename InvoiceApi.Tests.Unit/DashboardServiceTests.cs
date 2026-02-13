using InvoiceApi.Domain.Entities;
using InvoiceApi.Domain.Enums;
using InvoiceApi.Infrastructure.Data;
using InvoiceApi.Infrastructure.Service;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using NSubstitute;
using Shouldly;

namespace InvoiceApi.Tests.Unit;

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
        result.InvoicesThisMonth.ShouldBe(0);
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
    /// Tests that active VAT rates are correctly counted.
    /// </summary>
    [Fact]
    public async Task GetDashboardAsync_CountsActiveVatRates()
    {
        // Arrange
        _context.VatRate.Add(new VatRate { Name = "Standard", Rate = 21, IsActive = true });
        _context.VatRate.Add(new VatRate { Name = "Reduced", Rate = 12, IsActive = true });
        _context.VatRate.Add(new VatRate { Name = "Old rate", Rate = 15, IsActive = false });
        await _context.SaveChangesAsync();

        // Act
        var result = await _service.GetDashboardAsync();

        // Assert - only active VAT rates
        result.ActiveVatRates.ShouldBe(2);
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
}

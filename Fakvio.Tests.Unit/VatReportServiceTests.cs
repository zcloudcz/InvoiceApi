using Fakvio.Application.Service;
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
/// Unit tests for VatReportService — verifies output/input VAT aggregation,
/// tax liability calculation, revenue/expenses/profit, and period filtering by DUZP.
/// Uses InMemoryDatabase with seeded invoices and received invoices.
/// </summary>
public class VatReportServiceTests : IDisposable
{
    private readonly TenantDbContext _context;
    private readonly VatReportService _service;

    // Period: March 2026
    private readonly DateTime _periodFrom = new(2026, 3, 1, 0, 0, 0, DateTimeKind.Utc);
    private readonly DateTime _periodTo = new(2026, 3, 31, 0, 0, 0, DateTimeKind.Utc);

    public VatReportServiceTests()
    {
        var options = new DbContextOptionsBuilder<TenantDbContext>()
            .UseInMemoryDatabase(databaseName: Guid.NewGuid().ToString())
            .Options;

        _context = new TenantDbContext(options);

        // Stub IEpoSchemaProvider and ICurrencyService — not exercised in GetReportAsync tests.
        var schemaProvider  = Substitute.For<IEpoSchemaProvider>();
        var currencyService = Substitute.For<ICurrencyService>();
        currencyService
            .ConvertToCzkAsync(Arg.Any<decimal>(), Arg.Any<string>(), Arg.Any<DateOnly>(), Arg.Any<CancellationToken>())
            .Returns(call => Task.FromResult(call.ArgAt<decimal>(0)));

        // MasterDbContext and ITenantResolver are required by VatReportService (issue #39).
        // GetReportAsync does not use them, so we provide lightweight stubs.
        var masterOptions = new DbContextOptionsBuilder<MasterDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;
        var masterContext  = new MasterDbContext(masterOptions);
        var tenantResolver = Substitute.For<ITenantResolver>();
        tenantResolver.GetCurrentCompanyId().Returns((long?)null); // not needed for GetReportAsync

        var logger = Substitute.For<ILogger<VatReportService>>();
        _service = new VatReportService(_context, masterContext, tenantResolver, schemaProvider, currencyService, logger);

        SeedBaseData();
    }

    public void Dispose()
    {
        _context.Database.EnsureDeleted();
        _context.Dispose();
    }

    /// <summary>
    /// Seeds currency, VAT rates, and client entities for tests.
    /// </summary>
    private void SeedBaseData()
    {
        _context.Currency.Add(new Currency
        {
            Id = 1, Code = "CZK", Name = "Czech Koruna", Symbol = "Kč",
            DecimalPlaces = 2, SortOrder = 1, IsActive = true
        });
        _context.SaveChanges();

        _context.Client.Add(new Client
        {
            Id = 1, CompanyName = "Customer A", RegistrationNumber = "REG001",
            IsIssuer = false, IsActive = true
        });
        _context.SaveChanges();

        _context.Client.Add(new Client
        {
            Id = 2, CompanyName = "My Company", RegistrationNumber = "REG002",
            IsIssuer = true, IsActive = true
        });
        _context.SaveChanges();
    }

    /// <summary>
    /// Creates an issued invoice with items in the given period and status.
    /// </summary>
    private void SeedIssuedInvoice(
        DateTime taxableSupplyDate,
        EInvoiceStatus status,
        decimal unitPrice,
        decimal vatPercentage,
        int quantity = 1,
        EDocumentType docType = EDocumentType.Invoice)
    {
        var invoice = new Invoice
        {
            DocumentType = docType,
            Status = status,
            DocumentNumber = $"INV-{Guid.NewGuid().ToString()[..6]}",
            IssueDate = taxableSupplyDate,
            TaxableSupplyDate = taxableSupplyDate,
            DueDate = taxableSupplyDate.AddDays(14),
            ClientId = 1,
            IssuerId = 2,
            Issuer = _context.Client.Find(2L)!,
            CurrencyId = 1,
            TotalBeforeVat = quantity * unitPrice,
            TotalVat = quantity * unitPrice * (vatPercentage / 100m),
            TotalWithVat = quantity * unitPrice * (1 + vatPercentage / 100m),
            InvoiceItem = new List<InvoiceItem>()
        };

        invoice.InvoiceItem.Add(new InvoiceItem
        {
            OrderIndex = 1,
            Description = "Test item",
            Quantity = quantity,
            UnitPrice = unitPrice,
            VatRatePercentage = vatPercentage,
            TotalBeforeVat = quantity * unitPrice,
            VatAmount = quantity * unitPrice * (vatPercentage / 100m),
            TotalWithVat = quantity * unitPrice * (1 + vatPercentage / 100m)
        });

        _context.Invoice.Add(invoice);
        _context.SaveChanges();
    }

    /// <summary>
    /// Creates a received invoice with items in the given period and status.
    /// </summary>
    private void SeedReceivedInvoice(
        DateTime taxableSupplyDate,
        EReceivedInvoiceStatus status,
        decimal unitPrice,
        decimal vatPercentage,
        int quantity = 1)
    {
        var received = new ReceivedInvoice
        {
            DocumentNumber = $"REC-{Guid.NewGuid().ToString()[..6]}",
            Status = status,
            SupplierId = 1,
            IssueDate = taxableSupplyDate,
            ReceivedDate = taxableSupplyDate,
            DueDate = taxableSupplyDate.AddDays(14),
            TaxableSupplyDate = taxableSupplyDate,
            CurrencyId = 1,
            TotalBeforeVat = quantity * unitPrice,
            TotalVat = quantity * unitPrice * (vatPercentage / 100m),
            TotalWithVat = quantity * unitPrice * (1 + vatPercentage / 100m)
        };

        received.Items.Add(new ReceivedInvoiceItem
        {
            OrderIndex = 1,
            Description = "Test expense",
            Quantity = quantity,
            UnitPrice = unitPrice,
            VatRatePercentage = vatPercentage,
            TotalBeforeVat = quantity * unitPrice,
            VatAmount = quantity * unitPrice * (vatPercentage / 100m),
            TotalWithVat = quantity * unitPrice * (1 + vatPercentage / 100m)
        });

        _context.ReceivedInvoice.Add(received);
        _context.SaveChanges();
    }

    [Fact]
    public async Task GetReportAsync_EmptyPeriod_ReturnsZeros()
    {
        var report = await _service.GetReportAsync(_periodFrom, _periodTo);

        report.ShouldNotBeNull();
        report.TotalOutputVat.ShouldBe(0);
        report.TotalInputVat.ShouldBe(0);
        report.TaxLiability.ShouldBe(0);
        report.TotalRevenue.ShouldBe(0);
        report.TotalExpenses.ShouldBe(0);
        report.Profit.ShouldBe(0);
        report.IssuedInvoiceCount.ShouldBe(0);
        report.ReceivedInvoiceCount.ShouldBe(0);
    }

    [Fact]
    public async Task GetReportAsync_CreditNote_ReducesOutputVatRevenueAndIsCounted()
    {
        SeedIssuedInvoice(_periodFrom.AddDays(5), EInvoiceStatus.Completed, 1000, 21);
        // Stored positive (Fakvio does not enforce a sign) — must still reduce the totals.
        SeedIssuedInvoice(_periodFrom.AddDays(8), EInvoiceStatus.Completed, 400, 21, docType: EDocumentType.CreditNote);
        // Credit note in another period is not part of this one.
        SeedIssuedInvoice(_periodTo.AddDays(5), EInvoiceStatus.Completed, 400, 21, docType: EDocumentType.CreditNote);

        var report = await _service.GetReportAsync(_periodFrom, _periodTo);

        report.IssuedInvoiceCount.ShouldBe(2);
        report.TotalOutputVat.ShouldBe(126m); // (1000 - 400) * 0.21
        report.TotalRevenue.ShouldBe(600m);
        report.OutputVat.Single().BaseAmount.ShouldBe(600m);
    }

    [Fact]
    public async Task GetReportAsync_CreditNoteWithMixedSignRows_IsNegativeNetNotSumOfAbs()
    {
        // Rows -1000 and +600 (net -400) at 21 % → -400 base / -84 VAT, not -1600.
        SeedIssuedInvoice(_periodFrom.AddDays(8), EInvoiceStatus.Completed, -1000, 21, docType: EDocumentType.CreditNote);
        var cn = _context.Invoice.Include(i => i.InvoiceItem).Single();
        cn.InvoiceItem.Add(new InvoiceItem
        {
            OrderIndex = 2, Description = "Positive row", Quantity = 1, UnitPrice = 600,
            VatRatePercentage = 21, TotalBeforeVat = 600, VatAmount = 126, TotalWithVat = 726
        });
        _context.SaveChanges();

        var report = await _service.GetReportAsync(_periodFrom, _periodTo);

        report.OutputVat.Single().BaseAmount.ShouldBe(-400m);
        report.TotalOutputVat.ShouldBe(-84m);
    }

    [Fact]
    public async Task GetReportAsync_OutputVat_AggregatesCompletedInvoices()
    {
        // Two completed invoices in period
        SeedIssuedInvoice(_periodFrom.AddDays(5), EInvoiceStatus.Completed, 1000, 21);
        SeedIssuedInvoice(_periodFrom.AddDays(10), EInvoiceStatus.Paid, 2000, 21);

        var report = await _service.GetReportAsync(_periodFrom, _periodTo);

        report.IssuedInvoiceCount.ShouldBe(2);
        report.TotalOutputVat.ShouldBe(630m); // (1000+2000) * 0.21
        report.TotalRevenue.ShouldBe(3000m);
        report.OutputVat.Count.ShouldBe(1); // All same rate
        report.OutputVat[0].VatRatePercentage.ShouldBe(21m);
    }

    [Fact]
    public async Task GetReportAsync_ExcludesDraftAndDeletedInvoices()
    {
        SeedIssuedInvoice(_periodFrom.AddDays(1), EInvoiceStatus.Completed, 1000, 21);
        SeedIssuedInvoice(_periodFrom.AddDays(2), EInvoiceStatus.Draft, 5000, 21);
        SeedIssuedInvoice(_periodFrom.AddDays(3), EInvoiceStatus.Deleted, 5000, 21);

        var report = await _service.GetReportAsync(_periodFrom, _periodTo);

        report.IssuedInvoiceCount.ShouldBe(1);
        report.TotalRevenue.ShouldBe(1000m);
    }

    [Fact]
    public async Task GetReportAsync_InputVat_AggregatesApprovedAndPaid()
    {
        // Approved and Paid received invoices count, others don't
        SeedReceivedInvoice(_periodFrom.AddDays(5), EReceivedInvoiceStatus.Approved, 500, 21);
        SeedReceivedInvoice(_periodFrom.AddDays(10), EReceivedInvoiceStatus.Paid, 1000, 21);
        SeedReceivedInvoice(_periodFrom.AddDays(15), EReceivedInvoiceStatus.Received, 9999, 21); // excluded
        SeedReceivedInvoice(_periodFrom.AddDays(20), EReceivedInvoiceStatus.Rejected, 9999, 21); // excluded

        var report = await _service.GetReportAsync(_periodFrom, _periodTo);

        report.ReceivedInvoiceCount.ShouldBe(2);
        report.TotalInputVat.ShouldBe(315m); // (500+1000) * 0.21
        report.TotalExpenses.ShouldBe(1500m);
    }

    [Fact]
    public async Task GetReportAsync_TaxLiability_OutputMinusInput()
    {
        // Output: 1000 * 21% = 210 VAT
        SeedIssuedInvoice(_periodFrom.AddDays(5), EInvoiceStatus.Completed, 1000, 21);
        // Input: 500 * 21% = 105 VAT
        SeedReceivedInvoice(_periodFrom.AddDays(10), EReceivedInvoiceStatus.Approved, 500, 21);

        var report = await _service.GetReportAsync(_periodFrom, _periodTo);

        report.TotalOutputVat.ShouldBe(210m);
        report.TotalInputVat.ShouldBe(105m);
        report.TaxLiability.ShouldBe(105m); // Owe 105 to tax office
        report.Profit.ShouldBe(500m); // 1000 - 500
    }

    [Fact]
    public async Task GetReportAsync_NegativeTaxLiability_MeansRefund()
    {
        // Small output, big input
        SeedIssuedInvoice(_periodFrom.AddDays(5), EInvoiceStatus.Completed, 100, 21);
        SeedReceivedInvoice(_periodFrom.AddDays(10), EReceivedInvoiceStatus.Approved, 1000, 21);

        var report = await _service.GetReportAsync(_periodFrom, _periodTo);

        report.TaxLiability.ShouldBeLessThan(0); // Refund
    }

    [Fact]
    public async Task GetReportAsync_MultipleVatRates_GroupsSeparately()
    {
        SeedIssuedInvoice(_periodFrom.AddDays(1), EInvoiceStatus.Completed, 1000, 21);
        SeedIssuedInvoice(_periodFrom.AddDays(2), EInvoiceStatus.Completed, 1000, 15);

        var report = await _service.GetReportAsync(_periodFrom, _periodTo);

        report.OutputVat.Count.ShouldBe(2);
        // Ordered by VatRatePercentage descending
        report.OutputVat[0].VatRatePercentage.ShouldBe(21m);
        report.OutputVat[1].VatRatePercentage.ShouldBe(15m);
    }

    [Fact]
    public async Task GetReportAsync_OutsidePeriod_NotIncluded()
    {
        // Invoice in February (outside March period)
        SeedIssuedInvoice(new DateTime(2026, 2, 15, 0, 0, 0, DateTimeKind.Utc), EInvoiceStatus.Completed, 5000, 21);
        // Invoice in April (outside March period)
        SeedIssuedInvoice(new DateTime(2026, 4, 5, 0, 0, 0, DateTimeKind.Utc), EInvoiceStatus.Completed, 5000, 21);
        // Invoice in March (inside period)
        SeedIssuedInvoice(_periodFrom.AddDays(10), EInvoiceStatus.Completed, 1000, 21);

        var report = await _service.GetReportAsync(_periodFrom, _periodTo);

        report.IssuedInvoiceCount.ShouldBe(1);
        report.TotalRevenue.ShouldBe(1000m);
    }

    [Fact]
    public async Task GetReportAsync_PeriodDates_ArePreserved()
    {
        var report = await _service.GetReportAsync(_periodFrom, _periodTo);

        report.PeriodFrom.ShouldBe(_periodFrom);
        report.PeriodTo.ShouldBe(_periodTo);
    }

    // ── foreign currency (DEVGUIDE §4.17): stored document rate wins, no rate = failure ──

    private VatReportService ServiceWithRealConversion(Fakvio.Application.Service.IExchangeRateService rates)
    {
        var master = new MasterDbContext(new DbContextOptionsBuilder<MasterDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);
        var currency = new CurrencyService(null!, master, Substitute.For<ITenantResolver>(),
            Substitute.For<ILogger<CurrencyService>>(), rates);
        var resolver = Substitute.For<ITenantResolver>();
        return new VatReportService(_context, master, resolver, Substitute.For<IEpoSchemaProvider>(), currency,
            Substitute.For<ILogger<VatReportService>>());
    }

    /// <summary>EU B2B EUR invoice (exempt, 100 EUR, March 2026) — lands in the summary statement (SHV).</summary>
    private void SeedEurInvoice(decimal? storedRate)
    {
        _context.Currency.Add(new Currency { Id = 2, Code = "EUR", Name = "Euro", Symbol = "EUR", DecimalPlaces = 2, SortOrder = 2, IsActive = true });
        var de = new Client { Id = 3, CompanyName = "GmbH", RegistrationNumber = "R3", TaxNumber = "DE123456789", IsActive = true };
        de.Address.Add(new Address { ClientId = 3, Country = "DE", IsPrimary = true });
        _context.Client.Add(de);
        var duzp = new DateTime(2026, 3, 10, 0, 0, 0, DateTimeKind.Utc);
        var inv = new Invoice
        {
            DocumentType = EDocumentType.Invoice, Status = EInvoiceStatus.Completed, DocumentNumber = "EUR1",
            IssueDate = duzp, TaxableSupplyDate = duzp, DueDate = duzp.AddDays(14), ClientId = 3, IssuerId = 2,
            CurrencyId = 2, ExchangeRate = storedRate, TotalBeforeVat = 100, TotalWithVat = 100,
            InvoiceItem = new List<InvoiceItem>()
        };
        inv.InvoiceItem.Add(new InvoiceItem { OrderIndex = 1, Description = "x", Quantity = 1, UnitPrice = 100, TotalBeforeVat = 100,
            TotalWithVat = 100, VatAmount = 0, VatRegime = EVatRegime.Exempt });
        _context.Invoice.Add(inv);
        _context.SaveChanges();
    }

    private static Task<List<Fakvio.Contracts.Dto.VatReport.SummaryStatementRowDto>> Shv(VatReportService s) =>
        s.GetSummaryStatementRowsAsync(2026, 3, EVatPeriodType.Monthly);

    [Fact]
    public async Task EpoRows_ForeignInvoice_UsesRateStoredOnTheDocument_EvenWhenCnbDiffers()
    {
        SeedEurInvoice(storedRate: 25m); // e.g. a manual override: must match what the PDF printed
        var rates = Substitute.For<Fakvio.Application.Service.IExchangeRateService>();
        rates.GetRateAsync(Arg.Any<string>(), Arg.Any<DateOnly>(), Arg.Any<CancellationToken>())
            .Returns(new Fakvio.Contracts.Dto.ExchangeRate.ExchangeRateDto { CurrencyCode = "EUR", Amount = 1, Rate = 99m, RatePerUnit = 99m });

        var rows = await Shv(ServiceWithRealConversion(rates));

        rows.Single().TotalCzk.ShouldBe(2500);
        await rates.DidNotReceive().GetRateAsync(Arg.Any<string>(), Arg.Any<DateOnly>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task EpoRows_HistoricForeignInvoiceWithoutStoredRate_FallsBackToCnbRateAtDuzp()
    {
        SeedEurInvoice(storedRate: null);
        var rates = Substitute.For<Fakvio.Application.Service.IExchangeRateService>();
        rates.GetRateAsync("EUR", new DateOnly(2026, 3, 10), Arg.Any<CancellationToken>())
            .Returns(new Fakvio.Contracts.Dto.ExchangeRate.ExchangeRateDto { CurrencyCode = "EUR", Amount = 1, Rate = 24m, RatePerUnit = 24m });

        (await Shv(ServiceWithRealConversion(rates))).Single().TotalCzk.ShouldBe(2400);
    }

    [Fact]
    public async Task EpoRows_ForeignInvoiceWithNoRateAnywhere_Throws_NeverReportsForeignAmountAsCzk()
    {
        SeedEurInvoice(storedRate: null);
        var rates = Substitute.For<Fakvio.Application.Service.IExchangeRateService>(); // returns null

        await Should.ThrowAsync<Fakvio.Application.Exceptions.ExchangeRateUnavailableException>(
            () => Shv(ServiceWithRealConversion(rates)));
    }
}

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
/// Additional coverage for PR #29 (auto-issue tax receipt on Paid).
///
/// Covers gaps not addressed by AdvanceTaxReceiptServiceTests:
///   1. Idempotence guard treats deleted DPP as non-existent — a fresh DPP can be created.
///   2. Three-rate proportional split: rounding residual absorbed into last item so
///      Sum(item.TotalWithVat) == paidAmount exactly.
///   3. DPP aggregate totals (TotalWithVat / TotalBeforeVat / TotalVat) equal
///      the sum of the respective item columns.
///   4. Overpayment proportional split: VAT-rate ratios are preserved when
///      paidAmount > proforma.TotalWithVat.
///   5. MarkAsPaidAsync passes TotalWithVat (not the possibly-higher PaidAmount)
///      to IAdvanceTaxReceiptService when the Proforma was already partially matched.
/// </summary>
public class AdvanceTaxReceiptCoverageTests : IDisposable
{
    private readonly TenantDbContext _context;
    private readonly INumberSequenceService _numberSequenceService;
    private readonly AdvanceTaxReceiptService _sut;

    // Seed IDs reused across tests.
    private long _issuerId;
    private long _clientId;
    private long _currencyId;
    private long _vatRate21Id;
    private long _vatRate12Id;
    private long _vatRate0Id;

    public AdvanceTaxReceiptCoverageTests()
    {
        var options = new DbContextOptionsBuilder<TenantDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;

        _context = new TenantDbContext(options);

        _numberSequenceService = Substitute.For<INumberSequenceService>();
        _numberSequenceService
            .GenerateNextNumberForDocumentTypeAsync(
                EDocumentType.TaxReceiptForAdvance,
                Arg.Any<DateTime>(),
                Arg.Any<string?>(),
                Arg.Any<string?>(),
                Arg.Any<CancellationToken>())
            .Returns("DPP2026COV");

        _sut = new AdvanceTaxReceiptService(
            _context,
            _numberSequenceService,
            Substitute.For<ILogger<AdvanceTaxReceiptService>>());

        Seed();
    }

    public void Dispose()
    {
        _context.Database.EnsureDeleted();
        _context.Dispose();
        GC.SuppressFinalize(this);
    }

    // ─── Seed helpers ────────────────────────────────────────────────────────

    private void Seed()
    {
        var issuer = new Client
        {
            CompanyName = "Coverage Issuer s.r.o.",
            RegistrationNumber = "77700001",
            IsIssuer = true, IsActive = true,
            Address = new List<Address>(), Contact = new List<Contact>(),
            BankAccount = new List<BankAccount>(),
            CreatedAt = DateTime.UtcNow
        };
        _context.Client.Add(issuer);
        _context.SaveChanges();
        _issuerId = issuer.Id;

        var customer = new Client
        {
            CompanyName = "Coverage Customer s.r.o.",
            RegistrationNumber = "88800001",
            IsIssuer = false, IsActive = true,
            Address = new List<Address>(), Contact = new List<Contact>(),
            BankAccount = new List<BankAccount>(),
            CreatedAt = DateTime.UtcNow
        };
        _context.Client.Add(customer);
        _context.SaveChanges();
        _clientId = customer.Id;

        var currency = new Currency
        {
            Code = "CZK", Name = "Koruna", Symbol = "Kč",
            DecimalPlaces = 2, SortOrder = 1, IsActive = true
        };
        _context.Currency.Add(currency);
        _context.SaveChanges();
        _currencyId = currency.Id;

        var vatRate21 = new VatRate
        {
            Name = "21 %", Rate = 21m, IsDefault = true,
            ValidFrom = DateTime.UtcNow.AddYears(-1), IsActive = true
        };
        _context.VatRate.Add(vatRate21);
        _context.SaveChanges();
        _vatRate21Id = vatRate21.Id;

        var vatRate12 = new VatRate
        {
            Name = "12 %", Rate = 12m, IsDefault = false,
            ValidFrom = DateTime.UtcNow.AddYears(-1), IsActive = true
        };
        _context.VatRate.Add(vatRate12);
        _context.SaveChanges();
        _vatRate12Id = vatRate12.Id;

        var vatRate0 = new VatRate
        {
            Name = "0 %", Rate = 0m, IsDefault = false,
            ValidFrom = DateTime.UtcNow.AddYears(-1), IsActive = true
        };
        _context.VatRate.Add(vatRate0);
        _context.SaveChanges();
        _vatRate0Id = vatRate0.Id;
    }

    private Invoice SeedProformaWithItems(List<InvoiceItem> items)
    {
        decimal totalBeforeVat = items.Sum(i => i.TotalBeforeVat);
        decimal totalVat = items.Sum(i => i.VatAmount);
        decimal totalWithVat = items.Sum(i => i.TotalWithVat);

        var proforma = new Invoice
        {
            DocumentType = EDocumentType.Proforma,
            Status = EInvoiceStatus.Completed,
            DocumentNumber = $"PF-COV-{Guid.NewGuid():N}",
            IssueDate = new DateTime(2026, 5, 1, 0, 0, 0, DateTimeKind.Utc),
            DueDate = new DateTime(2026, 5, 15, 0, 0, 0, DateTimeKind.Utc),
            TaxableSupplyDate = new DateTime(2026, 5, 1, 0, 0, 0, DateTimeKind.Utc),
            IssuerId = _issuerId,
            ClientId = _clientId,
            CurrencyId = _currencyId,
            VariableSymbol = "COV0001",
            TotalBeforeVat = totalBeforeVat,
            TotalVat = totalVat,
            TotalWithVat = totalWithVat,
            InvoiceItem = items,
            CreatedAt = DateTime.UtcNow
        };

        _context.Invoice.Add(proforma);
        _context.SaveChanges();
        return proforma;
    }

    // ─── Test 1: Idempotence guard skips deleted DPPs ────────────────────────

    /// <summary>
    /// The idempotence guard excludes DPPs with Status=Deleted from consideration.
    /// When the only existing DPP for a proforma is deleted, a new one should be created.
    /// This covers the edge case where an accountant deletes an incorrectly issued DPP
    /// and needs to re-issue it.
    /// </summary>
    [Fact]
    public async Task IssueFromPaidProformaAsync_WhenExistingDppIsDeleted_CreatesNewDpp()
    {
        // Arrange — proforma with one item at 21 % VAT
        var proforma = SeedProformaWithItems(new List<InvoiceItem>
        {
            new()
            {
                OrderIndex = 1, Description = "Service", Quantity = 1, Unit = "pcs",
                UnitPrice = 1000m, VatRateId = _vatRate21Id, VatRatePercentage = 21m,
                TotalBeforeVat = 1000m, VatAmount = 210m, TotalWithVat = 1210m,
                CreatedAt = DateTime.UtcNow
            }
        });

        // Pre-seed a DELETED DPP for the same proforma — simulates a re-issuance scenario.
        var deletedDpp = new Invoice
        {
            DocumentType = EDocumentType.TaxReceiptForAdvance,
            Status = EInvoiceStatus.Deleted,   // <— deleted, must be ignored by idempotence guard
            DocumentNumber = "DPP-OLD-DELETED",
            IssueDate = new DateTime(2026, 5, 10, 0, 0, 0, DateTimeKind.Utc),
            TaxableSupplyDate = new DateTime(2026, 5, 10, 0, 0, 0, DateTimeKind.Utc),
            DueDate = new DateTime(2026, 5, 10, 0, 0, 0, DateTimeKind.Utc),
            IssuerId = _issuerId, ClientId = _clientId, CurrencyId = _currencyId,
            OriginalInvoiceId = proforma.Id,
            TotalBeforeVat = 1000m, TotalVat = 210m, TotalWithVat = 1210m,
            InvoiceItem = new List<InvoiceItem>(),
            CreatedAt = DateTime.UtcNow
        };
        _context.Invoice.Add(deletedDpp);
        _context.SaveChanges();

        var paymentDate = new DateTime(2026, 5, 10, 0, 0, 0, DateTimeKind.Utc);

        // Act — issue DPP; deleted DPP must be ignored, fresh one created
        var newDppId = await _sut.IssueFromPaidProformaAsync(proforma.Id, 1210m, paymentDate);

        // Assert — a new DPP was created (different ID from the deleted one)
        newDppId.ShouldNotBeNull();
        newDppId.ShouldNotBe(deletedDpp.Id);

        var newDpp = await _context.Invoice.FindAsync(newDppId);
        newDpp.ShouldNotBeNull();
        newDpp!.Status.ShouldNotBe(EInvoiceStatus.Deleted);
        newDpp.DocumentType.ShouldBe(EDocumentType.TaxReceiptForAdvance);
    }

    // ─── Test 2: Three-rate rounding residual absorbed into last item ─────────

    /// <summary>
    /// When the proforma has three VAT rates whose proportional shares do not divide
    /// evenly into paidAmount, the last item absorbs any rounding residual.
    /// Sum(item.TotalWithVat) must equal paidAmount exactly (no penny off).
    /// </summary>
    [Fact]
    public async Task IssueFromPaidProformaAsync_ThreeVatRates_SumOfItemsEqualsPaidAmount()
    {
        // Arrange — proforma with 3 items:
        //   21 %: 500 + 105 = 605 CZK
        //   12 %: 300 + 36  = 336 CZK
        //    0 %: 100 + 0   = 100 CZK
        // Total = 1041 CZK
        // Pay 1000 CZK (partial — but any amount works for rounding test)
        var proforma = SeedProformaWithItems(new List<InvoiceItem>
        {
            new()
            {
                OrderIndex = 1, Description = "A", Quantity = 1, Unit = "pcs",
                UnitPrice = 500m, VatRateId = _vatRate21Id, VatRatePercentage = 21m,
                TotalBeforeVat = 500m, VatAmount = 105m, TotalWithVat = 605m,
                CreatedAt = DateTime.UtcNow
            },
            new()
            {
                OrderIndex = 2, Description = "B", Quantity = 1, Unit = "pcs",
                UnitPrice = 300m, VatRateId = _vatRate12Id, VatRatePercentage = 12m,
                TotalBeforeVat = 300m, VatAmount = 36m, TotalWithVat = 336m,
                CreatedAt = DateTime.UtcNow
            },
            new()
            {
                OrderIndex = 3, Description = "C", Quantity = 1, Unit = "pcs",
                UnitPrice = 100m, VatRateId = _vatRate0Id, VatRatePercentage = 0m,
                TotalBeforeVat = 100m, VatAmount = 0m, TotalWithVat = 100m,
                CreatedAt = DateTime.UtcNow
            }
        });

        // Use a paidAmount that will trigger rounding residual across 3 items.
        // 1000 CZK against 1041 CZK total → shares: 605/1041, 336/1041, 100/1041
        // 605/1041 * 1000 = 581.17... → rounded to 581.17
        // 336/1041 * 1000 = 322.76... → rounded to 322.76
        // Residual for last: 1000 - 581.17 - 322.76 = 96.07
        const decimal paidAmount = 1000m;

        var paymentDate = new DateTime(2026, 5, 10, 0, 0, 0, DateTimeKind.Utc);

        // Act
        var dppId = await _sut.IssueFromPaidProformaAsync(proforma.Id, paidAmount, paymentDate);

        // Assert — sum of item totals must equal paidAmount exactly
        dppId.ShouldNotBeNull();
        var dpp = await _context.Invoice
            .Include(i => i.InvoiceItem)
            .FirstAsync(i => i.Id == dppId);

        dpp.InvoiceItem.Count.ShouldBe(3);
        var sumOfItems = dpp.InvoiceItem.Sum(i => i.TotalWithVat);
        sumOfItems.ShouldBe(paidAmount,
            $"Sum of item TotalWithVat ({sumOfItems}) must equal paidAmount ({paidAmount}) exactly");
    }

    // ─── Test 3: DPP aggregate totals match sum of item columns ──────────────

    /// <summary>
    /// The DPP entity's TotalWithVat / TotalBeforeVat / TotalVat must equal
    /// the respective sums of its InvoiceItems.
    /// This verifies the aggregate computation in AdvanceTaxReceiptService is consistent
    /// with the item list (no off-by-one or rounding divergence).
    /// </summary>
    [Fact]
    public async Task IssueFromPaidProformaAsync_AggregateTotals_MatchSumOfItems()
    {
        // Arrange — proforma with 2 VAT rates
        var proforma = SeedProformaWithItems(new List<InvoiceItem>
        {
            new()
            {
                OrderIndex = 1, Description = "S1", Quantity = 1, Unit = "pcs",
                UnitPrice = 800m, VatRateId = _vatRate21Id, VatRatePercentage = 21m,
                TotalBeforeVat = 800m, VatAmount = 168m, TotalWithVat = 968m,
                CreatedAt = DateTime.UtcNow
            },
            new()
            {
                OrderIndex = 2, Description = "S2", Quantity = 1, Unit = "pcs",
                UnitPrice = 400m, VatRateId = _vatRate12Id, VatRatePercentage = 12m,
                TotalBeforeVat = 400m, VatAmount = 48m, TotalWithVat = 448m,
                CreatedAt = DateTime.UtcNow
            }
        });

        // Use a non-round paidAmount that forces item-level rounding.
        const decimal paidAmount = 1113.33m;

        var paymentDate = new DateTime(2026, 5, 10, 0, 0, 0, DateTimeKind.Utc);

        // Act
        var dppId = await _sut.IssueFromPaidProformaAsync(proforma.Id, paidAmount, paymentDate);

        // Assert — DPP aggregate fields == item sums
        dppId.ShouldNotBeNull();
        var dpp = await _context.Invoice
            .Include(i => i.InvoiceItem)
            .FirstAsync(i => i.Id == dppId);

        var itemSumWithVat = dpp.InvoiceItem.Sum(i => i.TotalWithVat);
        var itemSumBeforeVat = dpp.InvoiceItem.Sum(i => i.TotalBeforeVat);
        var itemSumVat = dpp.InvoiceItem.Sum(i => i.VatAmount);

        dpp.TotalWithVat.ShouldBe(itemSumWithVat,
            "dpp.TotalWithVat must equal sum of item TotalWithVat");
        dpp.TotalBeforeVat.ShouldBe(itemSumBeforeVat,
            "dpp.TotalBeforeVat must equal sum of item TotalBeforeVat");
        dpp.TotalVat.ShouldBe(itemSumVat,
            "dpp.TotalVat must equal sum of item VatAmount");
    }

    // ─── Test 4: Overpayment preserves VAT-rate ratio ────────────────────────

    /// <summary>
    /// When paidAmount > proforma.TotalWithVat (overpayment), the proportional split
    /// must scale all VAT buckets proportionally. The ratio between the 21 % and 12 %
    /// items must match the original proforma ratio.
    ///
    /// Also verifies HasAlert=true and Sum(items) == paidAmount for the overpayment case.
    /// </summary>
    [Fact]
    public async Task IssueFromPaidProformaAsync_Overpayment_ProportionalRatioPreserved()
    {
        // Arrange — proforma: 1210 at 21 % + 560 at 12 % = 1770 total
        // Overpay with 2124 CZK (120 % of proforma total, clean ratio)
        var proforma = SeedProformaWithItems(new List<InvoiceItem>
        {
            new()
            {
                OrderIndex = 1, Description = "A", Quantity = 1, Unit = "pcs",
                UnitPrice = 1000m, VatRateId = _vatRate21Id, VatRatePercentage = 21m,
                TotalBeforeVat = 1000m, VatAmount = 210m, TotalWithVat = 1210m,
                CreatedAt = DateTime.UtcNow
            },
            new()
            {
                OrderIndex = 2, Description = "B", Quantity = 1, Unit = "pcs",
                UnitPrice = 500m, VatRateId = _vatRate12Id, VatRatePercentage = 12m,
                TotalBeforeVat = 500m, VatAmount = 60m, TotalWithVat = 560m,
                CreatedAt = DateTime.UtcNow
            }
        });

        const decimal paidAmount = 2124m;   // 120 % of 1770
        var paymentDate = new DateTime(2026, 5, 10, 0, 0, 0, DateTimeKind.Utc);

        // Act
        var dppId = await _sut.IssueFromPaidProformaAsync(proforma.Id, paidAmount, paymentDate);

        // Assert — HasAlert set, sum exact, proportional ratios preserved
        dppId.ShouldNotBeNull();
        var dpp = await _context.Invoice
            .Include(i => i.InvoiceItem)
            .FirstAsync(i => i.Id == dppId);

        dpp.HasAlert.ShouldBeTrue("overpayment must set HasAlert");
        dpp.TotalWithVat.ShouldBe(paidAmount);

        var sumOfItems = dpp.InvoiceItem.Sum(i => i.TotalWithVat);
        sumOfItems.ShouldBe(paidAmount, "sum of item TotalWithVat must equal paidAmount");

        // Proforma ratio: 21 % bucket = 1210/1770 ≈ 68.36 %, 12 % bucket = 560/1770 ≈ 31.64 %
        // Expected DPP: 21 % item = 1452 CZK (120 % of 1210), 12 % item = 672 CZK (120 % of 560)
        var item21 = dpp.InvoiceItem.First(i => i.VatRatePercentage == 21m);
        var item12 = dpp.InvoiceItem.First(i => i.VatRatePercentage == 12m);

        // Each VAT bucket scaled by 1.2×: 1210 * 1.2 = 1452, 560 * 1.2 = 672
        item21.TotalWithVat.ShouldBe(1452m);
        item12.TotalWithVat.ShouldBe(672m);
    }

    // ─── Test 5: MarkAsPaidAsync uses TotalWithVat for DPP call ─────────────

    /// <summary>
    /// InvoiceService.MarkAsPaidAsync passes invoice.TotalWithVat (not invoice.PaidAmount)
    /// to IAdvanceTaxReceiptService when triggering DPP issuance.
    ///
    /// This matters when PaidAmount was already set higher by a prior PaymentMatch
    /// (overpayment recorded by auto-matching). The manual MarkPaid path still calls DPP
    /// with TotalWithVat — the DPP service itself will set HasAlert if the amounts differ.
    /// </summary>
    [Fact]
    public async Task MarkAsPaidAsync_WhenProformaPaidAmountHigherFromPriorMatch_DppCalledWithTotalWithVat()
    {
        // Arrange — set up InvoiceService with a mock DPP service that records call arguments
        var mockDppService = Substitute.For<IAdvanceTaxReceiptService>();
        mockDppService
            .IssueFromPaidProformaAsync(Arg.Any<long>(), Arg.Any<decimal>(), Arg.Any<DateTime>(), Arg.Any<CancellationToken>())
            .Returns(99L);

        var mockSeqService = Substitute.For<INumberSequenceService>();
        mockSeqService
            .GenerateNextNumberForDocumentTypeAsync(
                Arg.Any<EDocumentType>(), Arg.Any<DateTime>(),
                Arg.Any<string?>(), Arg.Any<string?>(), Arg.Any<CancellationToken>())
            .Returns("INV2026-COVTEST");

        var invoiceService = new InvoiceService(
            _context, mockSeqService, mockDppService,
            Substitute.For<ILogger<InvoiceService>>());

        // Seed BillingSettings with OnAnyPayment so DPP fires on manual MarkPaid.
        _context.BillingSettings.Add(new BillingSettings
        {
            ClientId = _issuerId,
            DueDateCalculationType = EDueDateCalculationType.DaysFromIssue,
            DueDays = 14,
            AdvanceTaxReceiptMode = EAdvanceTaxReceiptMode.OnAnyPayment,
            CreatedAt = DateTime.UtcNow
        });
        _context.SaveChanges();

        // Proforma with TotalWithVat = 1210 but PaidAmount already 1500 from a prior PaymentMatch.
        var proforma = new Invoice
        {
            DocumentType = EDocumentType.Proforma,
            Status = EInvoiceStatus.Completed,
            DocumentNumber = "PF-COVTEST-001",
            IssueDate = new DateTime(2026, 5, 1, 0, 0, 0, DateTimeKind.Utc),
            DueDate = new DateTime(2026, 5, 15, 0, 0, 0, DateTimeKind.Utc),
            TaxableSupplyDate = new DateTime(2026, 5, 1, 0, 0, 0, DateTimeKind.Utc),
            IssuerId = _issuerId, ClientId = _clientId, CurrencyId = _currencyId,
            VariableSymbol = "COV99",
            TotalBeforeVat = 1000m, TotalVat = 210m, TotalWithVat = 1210m,
            PaidAmount = 1500m,  // higher from prior PaymentMatch (overpayment)
            InvoiceItem = new List<InvoiceItem>(), CreatedAt = DateTime.UtcNow
        };
        _context.Invoice.Add(proforma);
        _context.SaveChanges();

        var paymentDate = new DateTime(2026, 5, 10, 0, 0, 0, DateTimeKind.Utc);

        // Act
        await invoiceService.MarkAsPaidAsync(proforma.Id, paymentDate);

        // Assert — DPP service is called with TotalWithVat (1210), NOT PaidAmount (1500).
        // The service internally computes Math.Max(1500, 1210)=1500 for PaidAmount storage,
        // but the DPP call always uses TotalWithVat as the nominal DPP amount on this path.
        await mockDppService
            .Received(1)
            .IssueFromPaidProformaAsync(
                proforma.Id,
                1210m,   // TotalWithVat — not PaidAmount
                Arg.Any<DateTime>(),
                Arg.Any<CancellationToken>());
    }
}

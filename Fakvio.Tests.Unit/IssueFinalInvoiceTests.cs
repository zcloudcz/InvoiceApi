using Fakvio.Contracts.Dto.Invoice;
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
/// Unit tests for InvoiceService.IssueFinalInvoiceAsync.
/// Covers:
///   - Single VAT rate: one negative deduction row, correct amounts.
///   - Multi VAT rate (12 % + 21 %): two proportional deduction rows, sums correct.
///   - 1:N: second call respects RemainingAdvance; third call with excessive amount → error.
///   - Extra items (vícepráce): positive items added beside deduction — only advance is deducted.
///   - Validation: non-Proforma → 400, zero PaidAmount → 400, no remaining → 400.
///   - VAT rounding corner cases: uneven proportional split, total preserved.
/// </summary>
public class IssueFinalInvoiceTests : IDisposable
{
    private readonly TenantDbContext _context;
    private readonly InvoiceService _service;
    private readonly INumberSequenceService _numberSequence;

    // Monotonically increasing sequence counter so each generated number is unique.
    private int _seqCounter = 1000;

    public IssueFinalInvoiceTests()
    {
        var options = new DbContextOptionsBuilder<TenantDbContext>()
            .UseInMemoryDatabase(databaseName: Guid.NewGuid().ToString())
            .Options;

        _context = new TenantDbContext(options);
        var logger = Substitute.For<ILogger<InvoiceService>>();
        _numberSequence = Substitute.For<INumberSequenceService>();

        // Return a unique document number per call (avoids duplicate VariableSymbol conflicts).
        _numberSequence
            .GenerateNextNumberForDocumentTypeAsync(
                Arg.Any<EDocumentType>(), Arg.Any<DateTime>(),
                Arg.Any<string?>(), Arg.Any<string?>(),
                Arg.Any<CancellationToken>())
            .Returns(_ => (_seqCounter++).ToString());

        _service = new InvoiceService(_context, _numberSequence, Substitute.For<ITenantReadinessService>(), logger);

        SeedBaseData();
    }

    public void Dispose()
    {
        _context.Database.EnsureDeleted();
        _context.Dispose();
    }

    // ─── Seed helpers ─────────────────────────────────────────────────────────

    /// <summary>
    /// Seeds the minimum required reference data: customer, issuer (VAT payer), currency, VAT rates.
    /// </summary>
    private void SeedBaseData()
    {
        _context.Client.Add(new Client
        {
            Id = 1, CompanyName = "Customer A", RegistrationNumber = "REG001",
            IsIssuer = false, IsActive = true
        });
        _context.SaveChanges();

        _context.Client.Add(new Client
        {
            Id = 2, CompanyName = "My Company", RegistrationNumber = "REG002",
            IsIssuer = true, IsActive = true, IsVatPayer = true
        });
        _context.SaveChanges();

        _context.Currency.Add(new Currency
        {
            Id = 1, Code = "CZK", Name = "Czech Koruna", Symbol = "Kc",
            DecimalPlaces = 2, SortOrder = 1, IsActive = true
        });
        _context.SaveChanges();

        // Standard 21 % rate
        _context.VatRate.Add(new VatRate
        {
            Id = 1, Name = "Standard 21%", Rate = 21, IsDefault = true,
            ValidFrom = DateTime.UtcNow.AddYears(-1), IsActive = true
        });
        _context.SaveChanges();

        // Reduced 12 % rate
        _context.VatRate.Add(new VatRate
        {
            Id = 2, Name = "Reduced 12%", Rate = 12, IsDefault = false,
            ValidFrom = DateTime.UtcNow.AddYears(-1), IsActive = true
        });
        _context.SaveChanges();
    }

    /// <summary>
    /// Creates and persists a Proforma invoice with the given items.
    /// Returns the tracked Invoice entity (already saved).
    /// </summary>
    private Invoice SeedProforma(List<InvoiceItem> items, decimal paidAmount)
    {
        var proforma = new Invoice
        {
            DocumentType = EDocumentType.Proforma,
            Status = EInvoiceStatus.Paid,
            DocumentNumber = $"PF-{_seqCounter++}",
            IssueDate = new DateTime(2026, 3, 1, 0, 0, 0, DateTimeKind.Utc),
            TaxableSupplyDate = new DateTime(2026, 3, 1, 0, 0, 0, DateTimeKind.Utc),
            ClientId = 1,
            IssuerId = 2,
            CurrencyId = 1,
            PaidAmount = paidAmount,
            InvoiceItem = items
        };

        // Set totals from items.
        proforma.TotalBeforeVat = items.Where(i => !i.IsTextRow).Sum(i => i.TotalBeforeVat);
        proforma.TotalVat = items.Where(i => !i.IsTextRow).Sum(i => i.VatAmount);
        proforma.TotalWithVat = proforma.TotalBeforeVat + proforma.TotalVat;

        _context.Invoice.Add(proforma);
        _context.SaveChanges();

        return proforma;
    }

    /// <summary>
    /// Creates a single-rate proforma with one 21 % item.
    /// 1000 CZK base + 210 CZK VAT = 1210 CZK total (paid in full).
    /// </summary>
    private Invoice SeedSingleRateProforma()
    {
        return SeedProforma(new List<InvoiceItem>
        {
            new()
            {
                OrderIndex = 1,
                Description = "Consulting",
                Quantity = 1,
                Unit = "pcs",
                UnitPrice = 1000,
                VatRateId = 1,
                VatRatePercentage = 21,
                TotalBeforeVat = 1000,
                VatAmount = 210,
                TotalWithVat = 1210
            }
        }, paidAmount: 1210);
    }

    /// <summary>
    /// Creates a two-rate proforma: 800 base @12 % + 500 base @21 %.
    /// 12 %: 800 + 96 = 896 CZK.
    /// 21 %: 500 + 105 = 605 CZK.
    /// Total: 1501 CZK (paid in full).
    /// </summary>
    private Invoice SeedMultiRateProforma()
    {
        return SeedProforma(new List<InvoiceItem>
        {
            new()
            {
                OrderIndex = 1,
                Description = "Reduced-rate item",
                Quantity = 1, Unit = "pcs",
                UnitPrice = 800, VatRateId = 2, VatRatePercentage = 12,
                TotalBeforeVat = 800, VatAmount = 96, TotalWithVat = 896
            },
            new()
            {
                OrderIndex = 2,
                Description = "Standard-rate item",
                Quantity = 1, Unit = "pcs",
                UnitPrice = 500, VatRateId = 1, VatRatePercentage = 21,
                TotalBeforeVat = 500, VatAmount = 105, TotalWithVat = 605
            }
        }, paidAmount: 1501);
    }

    // ─── Helper: build a minimal IssueFinalInvoiceDto ─────────────────────────

    private IssueFinalInvoiceDto MinimalFinalInvoiceDto(decimal? deductionAmount = null)
    {
        return new IssueFinalInvoiceDto
        {
            DeductionAmount = deductionAmount,
            InvoiceItem = new List<CreateInvoiceItemDto>
            {
                new()
                {
                    OrderIndex = 1,
                    Description = "Final work",
                    Quantity = 1,
                    Unit = "pcs",
                    UnitPrice = 500,
                    VatRateId = 1,
                    VatRatePercentage = 21
                }
            }
        };
    }

    // =====================================================================
    // Validation tests
    // =====================================================================

    [Fact]
    public async Task IssueFinalInvoice_NonProforma_ThrowsInvalidOperation()
    {
        // Arrange — seed a regular Invoice (not a Proforma)
        var invoice = new Invoice
        {
            DocumentType = EDocumentType.Invoice,
            Status = EInvoiceStatus.Paid,
            DocumentNumber = "INV-001",
            IssueDate = new DateTime(2026, 3, 1, 0, 0, 0, DateTimeKind.Utc),
            TaxableSupplyDate = new DateTime(2026, 3, 1, 0, 0, 0, DateTimeKind.Utc),
            ClientId = 1, IssuerId = 2, CurrencyId = 1,
            PaidAmount = 1210, TotalBeforeVat = 1000, TotalVat = 210, TotalWithVat = 1210,
            InvoiceItem = new List<InvoiceItem>()
        };
        _context.Invoice.Add(invoice);
        _context.SaveChanges();

        // Act + Assert
        await Should.ThrowAsync<InvalidOperationException>(
            () => _service.IssueFinalInvoiceAsync(invoice.Id, MinimalFinalInvoiceDto()));
    }

    [Fact]
    public async Task IssueFinalInvoice_ProformaNotFound_ThrowsKeyNotFound()
    {
        await Should.ThrowAsync<KeyNotFoundException>(
            () => _service.IssueFinalInvoiceAsync(99999, MinimalFinalInvoiceDto()));
    }

    [Fact]
    public async Task IssueFinalInvoice_ZeroPaidAmount_ThrowsInvalidOperation()
    {
        // Arrange — proforma that has NOT been paid yet
        var proforma = SeedProforma(new List<InvoiceItem>
        {
            new()
            {
                OrderIndex = 1, Description = "Item", Quantity = 1, Unit = "pcs",
                UnitPrice = 1000, VatRatePercentage = 21, VatRateId = 1,
                TotalBeforeVat = 1000, VatAmount = 210, TotalWithVat = 1210
            }
        }, paidAmount: 0); // not paid

        // Act + Assert
        await Should.ThrowAsync<InvalidOperationException>(
            () => _service.IssueFinalInvoiceAsync(proforma.Id, MinimalFinalInvoiceDto()));
    }

    [Fact]
    public async Task IssueFinalInvoice_DeductionExceedsRemaining_ThrowsInvalidOperation()
    {
        // Arrange — proforma paid 1210, try to deduct 9999
        var proforma = SeedSingleRateProforma();

        // Act + Assert
        await Should.ThrowAsync<InvalidOperationException>(
            () => _service.IssueFinalInvoiceAsync(proforma.Id, MinimalFinalInvoiceDto(deductionAmount: 9999m)));
    }

    // =====================================================================
    // Single VAT rate — happy path
    // =====================================================================

    [Fact]
    public async Task IssueFinalInvoice_SingleRate_CreatesOneDeductionRow()
    {
        // Arrange — proforma: 1000 base + 210 VAT (21 %) = 1210 CZK paid.
        var proforma = SeedSingleRateProforma();
        var dto = MinimalFinalInvoiceDto(); // deduct full 1210

        // Act
        var result = await _service.IssueFinalInvoiceAsync(proforma.Id, dto);

        // Assert: one "real" item + one deduction item
        result.ShouldNotBeNull();
        result.DocumentType.ShouldBe(EDocumentType.Invoice);
        result.OriginalInvoiceId.ShouldBe(proforma.Id);

        var deductionRows = result.InvoiceItem.Where(i => i.TotalBeforeVat < 0).ToList();
        deductionRows.Count.ShouldBe(1, "exactly one deduction row for one VAT rate");

        var row = deductionRows.Single();
        row.VatRatePercentage.ShouldBe(21);

        // The deduction row (base + VAT) should equal the full advance paid.
        // row.TotalWithVat is negative; abs should equal 1210 (the deduction).
        Math.Round(Math.Abs(row.TotalWithVat), 2).ShouldBe(1210m);
    }

    [Fact]
    public async Task IssueFinalInvoice_SingleRate_TotalsAreCorrect()
    {
        // Proforma: 1000 base @21 % → paid 1210.
        // Final "real" item: 500 base @21 % → 500 + 105 = 605 with VAT.
        // Deduction row: -1000 base @21 % → -1000 + (-210) = -1210 with VAT.
        // Net total before VAT: 500 - 1000 = -500.
        // Net total VAT:         105 - 210 = -105.
        // Net total with VAT:   605 - 1210 = -605.
        var proforma = SeedSingleRateProforma();
        var dto = MinimalFinalInvoiceDto(); // 500 UnitPrice @21%, deduct full 1210

        var result = await _service.IssueFinalInvoiceAsync(proforma.Id, dto);

        // Real item: 500 * 1 = 500 base, 105 VAT, 605 with VAT.
        // Deduction: -1000 base, -210 VAT, -1210 with VAT.
        Math.Round(result.TotalBeforeVat, 2).ShouldBe(-500m);
        Math.Round(result.TotalVat, 2).ShouldBe(-105m);
        Math.Round(result.TotalWithVat, 2).ShouldBe(-605m);
    }

    [Fact]
    public async Task IssueFinalInvoice_SingleRate_PartialDeduction()
    {
        // Deduct only 605 (half of the 1210 advance).
        var proforma = SeedSingleRateProforma();
        var dto = MinimalFinalInvoiceDto(deductionAmount: 605m);

        var result = await _service.IssueFinalInvoiceAsync(proforma.Id, dto);

        var deductionRow = result.InvoiceItem.Single(i => i.TotalBeforeVat < 0);
        Math.Round(Math.Abs(deductionRow.TotalWithVat), 2).ShouldBe(605m,
            "deduction should equal the requested partial amount");

        // Remaining advance should now be 605.
        var remaining = await _service.GetRemainingAdvanceAsync(proforma.Id);
        Math.Round(remaining, 2).ShouldBe(605m);
    }

    // =====================================================================
    // Multi VAT rate (12 % + 21 %)
    // =====================================================================

    [Fact]
    public async Task IssueFinalInvoice_MultiRate_CreatesTwoDeductionRows()
    {
        // Proforma: 896 @12% + 605 @21% = 1501 total.
        var proforma = SeedMultiRateProforma();
        var dto = MinimalFinalInvoiceDto(); // deduct full 1501

        var result = await _service.IssueFinalInvoiceAsync(proforma.Id, dto);

        var deductionRows = result.InvoiceItem.Where(i => i.TotalBeforeVat < 0).ToList();
        deductionRows.Count.ShouldBe(2, "one deduction row per VAT rate");

        var rates = deductionRows.Select(r => r.VatRatePercentage).OrderBy(r => r).ToList();
        rates.ShouldBe(new List<decimal> { 12, 21 });
    }

    [Fact]
    public async Task IssueFinalInvoice_MultiRate_DeductionSumEqualsRequestedAmount()
    {
        // Proforma: 896 @12% + 605 @21% = 1501.
        var proforma = SeedMultiRateProforma();
        var dto = MinimalFinalInvoiceDto(); // deduct full 1501

        var result = await _service.IssueFinalInvoiceAsync(proforma.Id, dto);

        var deductionRows = result.InvoiceItem.Where(i => i.TotalBeforeVat < 0).ToList();

        // Sum of deduction TotalWithVat (negative) should equal -1501.
        var sumDeductionWithVat = deductionRows.Sum(r => r.TotalWithVat);
        Math.Round(sumDeductionWithVat, 2).ShouldBe(-1501m,
            "sum of all deduction rows (with VAT) must equal the advance paid");
    }

    [Fact]
    public async Task IssueFinalInvoice_MultiRate_ProportionalSplit()
    {
        // Proforma: 12 % share = 896/1501 ≈ 59.69 %; 21 % share = 605/1501 ≈ 40.31 %.
        // Deduction: 1501 * 0.5969 ≈ 896, 1501 * 0.4031 ≈ 605.
        var proforma = SeedMultiRateProforma();
        var dto = MinimalFinalInvoiceDto(); // full deduction

        var result = await _service.IssueFinalInvoiceAsync(proforma.Id, dto);

        var deductionRows = result.InvoiceItem.Where(i => i.TotalBeforeVat < 0).ToList();
        var row12 = deductionRows.Single(r => r.VatRatePercentage == 12);
        var row21 = deductionRows.Single(r => r.VatRatePercentage == 21);

        // Row @12 % should absorb the 12 % share ≈ 896.
        Math.Round(Math.Abs(row12.TotalWithVat), 2).ShouldBe(896m);
        // Row @21 % should absorb the 21 % share ≈ 605.
        Math.Round(Math.Abs(row21.TotalWithVat), 2).ShouldBe(605m);
    }

    [Fact]
    public async Task IssueFinalInvoice_MultiRate_EachRowHasCorrectVatAmount()
    {
        // Verify that the VAT inside each deduction row is calculated from its base correctly.
        var proforma = SeedMultiRateProforma();
        var dto = MinimalFinalInvoiceDto();

        var result = await _service.IssueFinalInvoiceAsync(proforma.Id, dto);

        var deductionRows = result.InvoiceItem.Where(i => i.TotalBeforeVat < 0).ToList();
        foreach (var row in deductionRows)
        {
            // TotalWithVat = TotalBeforeVat + VatAmount (all negative).
            var expectedVat = Math.Round(row.TotalBeforeVat * (row.VatRatePercentage / 100m), 2);
            // Allow 1-cent rounding tolerance — back-calculation from TotalWithVat may differ by 0.01.
            Math.Abs(row.VatAmount - expectedVat).ShouldBeLessThanOrEqualTo(0.01m,
                $"VAT amount for rate {row.VatRatePercentage}% should be within 1 cent of base × rate");
        }
    }

    // =====================================================================
    // 1:N — multiple final invoices from one proforma
    // =====================================================================

    [Fact]
    public async Task IssueFinalInvoice_1ToN_SecondCallRespectedRemainingAdvance()
    {
        // Proforma: 1210 paid (single rate 21 %).
        // First final invoice: deduct 605 → remaining = 605.
        // Second final invoice: deduct remaining 605 → remaining = 0.
        var proforma = SeedSingleRateProforma();

        // First call — partial deduction
        await _service.IssueFinalInvoiceAsync(proforma.Id, MinimalFinalInvoiceDto(deductionAmount: 605m));

        var remainingAfterFirst = await _service.GetRemainingAdvanceAsync(proforma.Id);
        Math.Round(remainingAfterFirst, 2).ShouldBe(605m);

        // Second call — deduct the rest
        await _service.IssueFinalInvoiceAsync(proforma.Id, MinimalFinalInvoiceDto(deductionAmount: 605m));

        var remainingAfterSecond = await _service.GetRemainingAdvanceAsync(proforma.Id);
        Math.Round(remainingAfterSecond, 2).ShouldBe(0m);
    }

    [Fact]
    public async Task IssueFinalInvoice_1ToN_ThirdCallExceedsRemaining_ThrowsInvalidOperation()
    {
        // Proforma: 1210 paid.
        // Two 605 deductions exhaust the advance.
        // Third call for any positive amount → error.
        var proforma = SeedSingleRateProforma();

        await _service.IssueFinalInvoiceAsync(proforma.Id, MinimalFinalInvoiceDto(deductionAmount: 605m));
        await _service.IssueFinalInvoiceAsync(proforma.Id, MinimalFinalInvoiceDto(deductionAmount: 605m));

        await Should.ThrowAsync<InvalidOperationException>(
            () => _service.IssueFinalInvoiceAsync(proforma.Id, MinimalFinalInvoiceDto(deductionAmount: 1m)));
    }

    [Fact]
    public async Task IssueFinalInvoice_1ToN_DefaultDeductionUsesRemainder()
    {
        // If DeductionAmount is null, the service should deduct the full remaining amount.
        var proforma = SeedSingleRateProforma(); // 1210 paid

        // First call with explicit partial deduction
        await _service.IssueFinalInvoiceAsync(proforma.Id, MinimalFinalInvoiceDto(deductionAmount: 500m));

        // Second call with null DeductionAmount → should deduct remaining 710.
        var secondResult = await _service.IssueFinalInvoiceAsync(proforma.Id, MinimalFinalInvoiceDto(deductionAmount: null));

        var deductionRow = secondResult.InvoiceItem.Single(i => i.TotalBeforeVat < 0);
        Math.Round(Math.Abs(deductionRow.TotalWithVat), 2).ShouldBe(710m,
            "default deduction should equal the remaining advance");
    }

    // =====================================================================
    // Extra items (vícepráce) — additional work beyond the advance
    // =====================================================================

    [Fact]
    public async Task IssueFinalInvoice_ExtraItems_DeductionEqualsAdvanceNotTotal()
    {
        // Proforma: 1210 paid (1000 base @21 %).
        // Final invoice has TWO extra items totalling 2000 base @21 % + advance deduction.
        // The deduction should be exactly 1210 (the advance) — NOT the final invoice total.
        var proforma = SeedSingleRateProforma();

        var dto = new IssueFinalInvoiceDto
        {
            DeductionAmount = null, // deduct full advance
            InvoiceItem = new List<CreateInvoiceItemDto>
            {
                new()
                {
                    OrderIndex = 1, Description = "Extra work item 1",
                    Quantity = 1, Unit = "pcs", UnitPrice = 1000,
                    VatRateId = 1, VatRatePercentage = 21
                },
                new()
                {
                    OrderIndex = 2, Description = "Extra work item 2",
                    Quantity = 1, Unit = "pcs", UnitPrice = 1000,
                    VatRateId = 1, VatRatePercentage = 21
                }
            }
        };

        var result = await _service.IssueFinalInvoiceAsync(proforma.Id, dto);

        // The deduction row should equal the advance (1210), not the final invoice total.
        var deductionRow = result.InvoiceItem.Single(i => i.TotalBeforeVat < 0);
        Math.Round(Math.Abs(deductionRow.TotalWithVat), 2).ShouldBe(1210m);

        // Positive items: 2 × 1000 = 2000 base + 2 × 210 = 420 VAT = 2420 with VAT.
        // Deduction: -1000 base - 210 VAT = -1210 with VAT.
        // Net: 1000 base, 210 VAT, 1210 with VAT.
        Math.Round(result.TotalBeforeVat, 2).ShouldBe(1000m);
        Math.Round(result.TotalVat, 2).ShouldBe(210m);
        Math.Round(result.TotalWithVat, 2).ShouldBe(1210m);
    }

    // =====================================================================
    // GetRemainingAdvanceAsync
    // =====================================================================

    [Fact]
    public async Task GetRemainingAdvance_NoFinalInvoices_ReturnsFullPaidAmount()
    {
        var proforma = SeedSingleRateProforma(); // 1210 paid, no deductions yet

        var remaining = await _service.GetRemainingAdvanceAsync(proforma.Id);
        Math.Round(remaining, 2).ShouldBe(1210m);
    }

    [Fact]
    public async Task GetRemainingAdvance_AfterFullDeduction_ReturnsZero()
    {
        var proforma = SeedSingleRateProforma();
        await _service.IssueFinalInvoiceAsync(proforma.Id, MinimalFinalInvoiceDto()); // full deduction

        var remaining = await _service.GetRemainingAdvanceAsync(proforma.Id);
        Math.Round(remaining, 2).ShouldBe(0m);
    }

    [Fact]
    public async Task GetRemainingAdvance_NonExistentProforma_ReturnsZero()
    {
        var remaining = await _service.GetRemainingAdvanceAsync(99999);
        remaining.ShouldBe(0m);
    }

    // =====================================================================
    // Rounding corner case — 3-way uneven proportional split
    // =====================================================================

    [Fact]
    public async Task IssueFinalInvoice_ThreeRates_RoundingResidualPreservesTotalDeduction()
    {
        // Use VatRate IDs 1 and 2, plus a third rate at 0 %.
        _context.VatRate.Add(new VatRate
        {
            Id = 3, Name = "Zero 0%", Rate = 0, IsDefault = false,
            ValidFrom = DateTime.UtcNow.AddYears(-1), IsActive = true
        });
        _context.SaveChanges();

        // Three items with equal TotalWithVat: 333 each (total 999 — intentionally odd).
        var proforma = SeedProforma(new List<InvoiceItem>
        {
            new()
            {
                OrderIndex = 1, Description = "A", Quantity = 1, Unit = "pcs",
                UnitPrice = 333m, VatRateId = 1, VatRatePercentage = 21,
                TotalBeforeVat = 333m, VatAmount = 69.93m, TotalWithVat = 333m
            },
            new()
            {
                OrderIndex = 2, Description = "B", Quantity = 1, Unit = "pcs",
                UnitPrice = 333m, VatRateId = 2, VatRatePercentage = 12,
                TotalBeforeVat = 333m, VatAmount = 39.96m, TotalWithVat = 333m
            },
            new()
            {
                OrderIndex = 3, Description = "C", Quantity = 1, Unit = "pcs",
                UnitPrice = 333m, VatRateId = 3, VatRatePercentage = 0,
                TotalBeforeVat = 333m, VatAmount = 0m, TotalWithVat = 333m
            }
        }, paidAmount: 999m);

        var dto = MinimalFinalInvoiceDto(); // deduct full 999

        var result = await _service.IssueFinalInvoiceAsync(proforma.Id, dto);

        // The sum of deduction rows (with VAT) must equal exactly -999 WITHOUT any Math.Round wrapper.
        // Previously the test used Math.Round(sumDeduction, 2) which masked drift caused by
        // unrounded VatAmount in CreateInvoiceAsync. Now VatAmount is rounded at the source
        // (Math.Round(..., 2, MidpointRounding.AwayFromZero)) so the raw sum must be exact.
        var deductionRows = result.InvoiceItem.Where(i => i.TotalBeforeVat < 0).ToList();
        var sumDeduction = deductionRows.Sum(r => r.TotalWithVat);
        sumDeduction.ShouldBe(-999m,
            "largest-remainder rounding + rounded VatAmount must ensure deduction rows sum exactly to the requested amount");
    }
}

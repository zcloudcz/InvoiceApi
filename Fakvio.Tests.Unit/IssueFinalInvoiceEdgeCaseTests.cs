// ============================================================================
// IssueFinalInvoiceEdgeCaseTests — coverage for PR #76 (issue #30).
//
// These tests exercise the gaps not covered by the existing IssueFinalInvoiceTests
// (which focuses on service-layer logic):
//
//   Service edge cases not covered in IssueFinalInvoiceTests
//      - Deleted final invoice deduction rows are excluded from RemainingAdvance calculation
//        (GetAlreadyDeductedAmountAsync filter: Status != Deleted)
//      - Text-only proforma items (all IsTextRow = true) → BuildDeductionItems fallback
//        produces a single 0% deduction row
// ============================================================================

using System.Security.Claims;
using System.Text;
using System.Text.Json;
using Fakvio.API.Controller;
using Fakvio.Application.Service;
using Fakvio.Contracts.Dto.Invoice;
using Fakvio.Domain.Entities;
using Fakvio.Domain.Enums;
using Fakvio.Infrastructure.Data;
using Fakvio.Infrastructure.Service;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using NSubstitute;
using NSubstitute.ExceptionExtensions;
using Shouldly;

namespace Fakvio.Tests.Unit;

// ============================================================================
// 3. Service edge cases — not covered in IssueFinalInvoiceTests
// ============================================================================

/// <summary>
/// Additional service-level tests for behaviours that the main IssueFinalInvoiceTests
/// class does not exercise:
///   - Deleted final invoices must not count toward already-deducted amount
///   - Text-only proforma items → BuildDeductionItems single 0% fallback row
/// </summary>
public class IssueFinalInvoiceEdgeCaseTests : IDisposable
{
    private readonly TenantDbContext _context;
    private readonly InvoiceService _service;
    private readonly INumberSequenceService _numberSequence;
    private int _seqCounter = 5000;

    public IssueFinalInvoiceEdgeCaseTests()
    {
        var options = new DbContextOptionsBuilder<TenantDbContext>()
            .UseInMemoryDatabase(databaseName: Guid.NewGuid().ToString())
            .Options;

        _context = new TenantDbContext(options);
        var logger = Substitute.For<ILogger<InvoiceService>>();
        _numberSequence = Substitute.For<INumberSequenceService>();

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

    // ── Seed helpers ─────────────────────────────────────────────────────

    private void SeedBaseData()
    {
        _context.Client.Add(new Client
        {
            Id = 10, CompanyName = "Test Customer", RegistrationNumber = "EDGE001",
            IsIssuer = false, IsActive = true
        });
        _context.SaveChanges();

        _context.Client.Add(new Client
        {
            Id = 11, CompanyName = "Test Issuer", RegistrationNumber = "EDGE002",
            IsIssuer = true, IsActive = true, IsVatPayer = true
        });
        _context.SaveChanges();

        _context.Currency.Add(new Currency
        {
            Id = 10, Code = "CZK", Name = "Czech Koruna", Symbol = "Kc",
            DecimalPlaces = 2, SortOrder = 1, IsActive = true
        });
        _context.SaveChanges();

        _context.VatRate.Add(new VatRate
        {
            Id = 10, Name = "Standard 21%", Rate = 21, IsDefault = true,
            ValidFrom = DateTime.UtcNow.AddYears(-1), IsActive = true
        });
        _context.SaveChanges();
    }

    /// <summary>
    /// Creates and saves a Proforma with one 21% item, 1210 CZK paid.
    /// </summary>
    private Invoice SeedSingleRateProforma()
    {
        var proforma = new Invoice
        {
            DocumentType = EDocumentType.Proforma,
            Status = EInvoiceStatus.Paid,
            DocumentNumber = $"PF-EDGE-{_seqCounter++}",
            IssueDate = new DateTime(2026, 3, 1, 0, 0, 0, DateTimeKind.Utc),
            TaxableSupplyDate = new DateTime(2026, 3, 1, 0, 0, 0, DateTimeKind.Utc),
            ClientId = 10,
            IssuerId = 11,
            CurrencyId = 10,
            PaidAmount = 1210m,
            TotalBeforeVat = 1000m,
            TotalVat = 210m,
            TotalWithVat = 1210m,
            InvoiceItem = new List<InvoiceItem>
            {
                new()
                {
                    OrderIndex = 1, Description = "Consulting",
                    Quantity = 1, Unit = "pcs",
                    UnitPrice = 1000m, VatRateId = 10, VatRatePercentage = 21,
                    TotalBeforeVat = 1000m, VatAmount = 210m, TotalWithVat = 1210m
                }
            }
        };

        _context.Invoice.Add(proforma);
        _context.SaveChanges();
        return proforma;
    }

    private IssueFinalInvoiceDto MinimalDto(decimal? deductionAmount = null) =>
        new()
        {
            DeductionAmount = deductionAmount,
            InvoiceItem = new List<CreateInvoiceItemDto>
            {
                new()
                {
                    OrderIndex = 1, Description = "Work",
                    Quantity = 1, Unit = "pcs", UnitPrice = 100m,
                    VatRateId = 10, VatRatePercentage = 21
                }
            }
        };

    // ── Test: Deleted invoice deduction rows are excluded ────────────────

    /// <summary>
    /// Verifies that the GetAlreadyDeductedAmountAsync helper (called inside
    /// both IssueFinalInvoiceAsync and GetRemainingAdvanceAsync) ignores deduction
    /// rows on Deleted final invoices.
    ///
    /// Scenario:
    ///  - Proforma paid 1210.
    ///  - First final invoice deducts 605 → then gets Deleted.
    ///  - Remaining advance should still be 1210 (the deleted deduction does not count).
    ///  - A second call with full deduction of 1210 must succeed (not be blocked).
    /// </summary>
    [Fact]
    public async Task GetRemainingAdvance_DeletedFinalInvoice_NotCountedAsDeducted()
    {
        // Arrange — proforma 1210 paid
        var proforma = SeedSingleRateProforma();

        // First final invoice — deducts 605
        var firstFinal = await _service.IssueFinalInvoiceAsync(
            proforma.Id, MinimalDto(deductionAmount: 605m));

        // Mark that first final invoice as Deleted (simulates user deletion)
        var finalEntity = _context.Invoice.Single(i => i.Id == firstFinal.Id);
        finalEntity.Status = EInvoiceStatus.Deleted;
        _context.SaveChanges();

        // Act — remaining advance should ignore the deleted deduction
        var remaining = await _service.GetRemainingAdvanceAsync(proforma.Id);

        // Assert — deleted deduction is excluded → full 1210 is still available
        Math.Round(remaining, 2).ShouldBe(1210m,
            "a deleted final invoice's deduction rows must not reduce the remaining advance");
    }

    [Fact]
    public async Task IssueFinalInvoice_AfterDeletedDeduction_AllowsFullDeductionAgain()
    {
        // Arrange — proforma 1210 paid; first final deducts 605 then gets deleted
        var proforma = SeedSingleRateProforma();

        var firstFinal = await _service.IssueFinalInvoiceAsync(
            proforma.Id, MinimalDto(deductionAmount: 605m));

        var finalEntity = _context.Invoice.Single(i => i.Id == firstFinal.Id);
        finalEntity.Status = EInvoiceStatus.Deleted;
        _context.SaveChanges();

        // Act — issue another final invoice deducting the full 1210 (should succeed)
        var secondFinal = await _service.IssueFinalInvoiceAsync(
            proforma.Id, MinimalDto(deductionAmount: 1210m));

        // Assert — second final invoice was created, deduction row equals 1210
        secondFinal.ShouldNotBeNull();
        var deductionRow = secondFinal.InvoiceItem.Single(i => i.TotalBeforeVat < 0);
        Math.Round(Math.Abs(deductionRow.TotalWithVat), 2).ShouldBe(1210m);
    }

    // ── Test: BuildDeductionItems text-only proforma fallback ────────────

    /// <summary>
    /// Verifies that a proforma whose items are ALL text rows (IsTextRow = true)
    /// produces a single 0% deduction row as a fallback (no division-by-zero,
    /// no missing deduction).
    ///
    /// "Text rows" are decorative rows with no monetary value. When all proforma items
    /// are text rows, the VAT group list is empty and BuildDeductionItems falls back
    /// to a single 0% row equal to the full requested deduction.
    ///
    /// NOTE: The issuer here is a non-VAT payer. For VAT-paying issuers the
    /// CreateInvoiceAsync validation rejects items without VatRateId, which is
    /// intentional (a VAT-paying issuer should not have text-only proformas
    /// with non-zero PaidAmount in realistic scenarios).
    /// </summary>
    [Fact]
    public async Task IssueFinalInvoice_TextOnlyProformaItems_ProducesSingleZeroRateDeduction()
    {
        // Seed a non-VAT-payer issuer so the 0% fallback row (VatRateId = null)
        // passes the VAT-payer validation inside CreateInvoiceAsync.
        _context.Client.Add(new Client
        {
            Id = 20, CompanyName = "Non-VAT Issuer", RegistrationNumber = "NOVAT001",
            IsIssuer = true, IsActive = true, IsVatPayer = false  // ← not a VAT payer
        });
        _context.SaveChanges();

        // Arrange — proforma whose only item is a text (header) row
        var proforma = new Invoice
        {
            DocumentType = EDocumentType.Proforma,
            Status = EInvoiceStatus.Paid,
            DocumentNumber = $"PF-TEXT-{_seqCounter++}",
            IssueDate = new DateTime(2026, 3, 1, 0, 0, 0, DateTimeKind.Utc),
            TaxableSupplyDate = new DateTime(2026, 3, 1, 0, 0, 0, DateTimeKind.Utc),
            ClientId = 10,       // customer from SeedBaseData
            IssuerId = 20,       // non-VAT payer issuer
            CurrencyId = 10,
            PaidAmount = 500m,
            TotalBeforeVat = 0m,
            TotalVat = 0m,
            TotalWithVat = 0m,
            InvoiceItem = new List<InvoiceItem>
            {
                new()
                {
                    OrderIndex = 1,
                    Description = "==== Section header ====",
                    IsTextRow = true,         // key: text row, no monetary value
                    Quantity = 0, Unit = "pcs",
                    UnitPrice = 0, VatRatePercentage = 0,
                    TotalBeforeVat = 0, VatAmount = 0, TotalWithVat = 0
                }
            }
        };

        _context.Invoice.Add(proforma);
        _context.SaveChanges();

        var dto = new IssueFinalInvoiceDto
        {
            DeductionAmount = 500m, // deduct full paid amount
            InvoiceItem = new List<CreateInvoiceItemDto>
            {
                new()
                {
                    OrderIndex = 1, Description = "Actual work",
                    Quantity = 1, Unit = "pcs", UnitPrice = 1000m,
                    VatRateId = null,   // non-VAT payer — no VAT rate needed
                    VatRatePercentage = 0
                }
            }
        };

        // Act
        var result = await _service.IssueFinalInvoiceAsync(proforma.Id, dto);

        // Assert — exactly one deduction row produced
        var deductionRows = result.InvoiceItem.Where(i => i.TotalBeforeVat < 0).ToList();
        deductionRows.Count.ShouldBe(1, "text-only proforma falls back to one 0% deduction row");

        // The fallback row uses 0% VAT (no VAT rate → VatRatePercentage = 0)
        var fallbackRow = deductionRows.Single();
        fallbackRow.VatRatePercentage.ShouldBe(0m,
            "fallback deduction row for text-only proforma must be at 0% VAT");

        // TotalWithVat of the deduction equals the requested amount (negative)
        Math.Round(Math.Abs(fallbackRow.TotalWithVat), 2).ShouldBe(500m,
            "fallback row must deduct the full requested 500 CZK");
    }
}

using Fakvio.Application.Common.Helpers;
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
/// Unit tests for <see cref="InvoiceService.CopyInvoiceAsync"/>.
///
/// Each test uses an isolated in-memory database (unique Guid name) to prevent
/// data leakage between runs. The class implements IDisposable so EF Core cleans
/// up after the test class.
///
/// Mocking strategy:
/// - INumberSequenceService is mocked via NSubstitute.
///   Default: returns "COPY2026001" so VS tests can verify digit extraction.
/// - ILogger is mocked — no log assertions; only prevents NREs in production code.
/// </summary>
public class InvoiceServiceCopyTests : IDisposable
{
    // ─── Dependencies ────────────────────────────────────────────────────────

    private readonly TenantDbContext _context;
    private readonly InvoiceService _service;
    private readonly ILogger<InvoiceService> _logger;
    private readonly INumberSequenceService _numberSequence;

    // ─── Well-known seed IDs ─────────────────────────────────────────────────

    private const long CustomerId = 1;
    private const long IssuerId   = 2;
    private const long CurrencyId = 1;
    private const long VatRateId  = 1;

    // Document number returned by the mock — used in VS assertions.
    // Digits-only portion must differ from the source invoice's VariableSymbol ("2026001")
    // to avoid a duplicate VS collision in CreateInvoiceAsync's pre-save check.
    // "COPY9999001" → digits → "9999001" — unique from source VS.
    private const string MockDocumentNumber = "COPY9999001";

    // ─── Constructor & Dispose ───────────────────────────────────────────────

    /// <summary>
    /// Sets up the isolated in-memory DB, mocks and seeds reference data.
    /// </summary>
    public InvoiceServiceCopyTests()
    {
        var options = new DbContextOptionsBuilder<TenantDbContext>()
            .UseInMemoryDatabase(databaseName: Guid.NewGuid().ToString())
            .Options;

        _context = new TenantDbContext(options);
        _logger = Substitute.For<ILogger<InvoiceService>>();
        _numberSequence = Substitute.For<INumberSequenceService>();

        // Configure mock to return a known document number.
        // CreateInvoiceAsync calls GenerateNextNumberForDocumentTypeAsync when no
        // explicit NumberSequenceId is given (the default path for CopyInvoiceAsync).
        _numberSequence
            .GenerateNextNumberForDocumentTypeAsync(
                Arg.Any<EDocumentType>(), Arg.Any<DateTime>(),
                Arg.Any<string?>(), Arg.Any<string?>(),
                Arg.Any<CancellationToken>())
            .Returns(MockDocumentNumber);

        _numberSequence
            .GenerateNextNumberAsync(
                Arg.Any<long>(), Arg.Any<DateTime>(), Arg.Any<CancellationToken>())
            .Returns(MockDocumentNumber);

        _service = new InvoiceService(_context, _numberSequence, Substitute.For<ITenantReadinessService>(), _logger);

        SeedReferenceData();
    }

    public void Dispose()
    {
        _context.Database.EnsureDeleted();
        _context.Dispose();
    }

    // ─── Seed Helpers ────────────────────────────────────────────────────────

    /// <summary>
    /// Seeds the minimum reference entities (Client, Currency, VatRate) required
    /// by Invoice foreign-key constraints. Invoices are NOT seeded here —
    /// each test creates its own source invoice via <see cref="CreateSourceInvoice"/>.
    /// </summary>
    private void SeedReferenceData()
    {
        // Customer
        _context.Client.Add(new Client
        {
            Id = CustomerId,
            CompanyName = "Test Customer",
            RegistrationNumber = "CUST-001",
            IsIssuer = false,
            IsActive = true
        });
        _context.SaveChanges();

        // Issuer (non-VAT payer — keeps items simple, no VatRateId required)
        _context.Client.Add(new Client
        {
            Id = IssuerId,
            CompanyName = "Test Issuer Ltd.",
            RegistrationNumber = "ISS-001",
            IsIssuer = true,
            IsActive = true,
            IsVatPayer = false
        });
        _context.SaveChanges();

        // Currency
        _context.Currency.Add(new Currency
        {
            Id = CurrencyId,
            Code = "CZK",
            Name = "Czech Koruna",
            Symbol = "Kc",
            DecimalPlaces = 2,
            SortOrder = 1,
            IsActive = true
        });
        _context.SaveChanges();

        // VAT rate (needed for ReverseCharge tests)
        _context.VatRate.Add(new VatRate
        {
            Id = VatRateId,
            Name = "Standard 21%",
            Rate = 21,
            IsDefault = true,
            ValidFrom = DateTime.UtcNow.AddYears(-1),
            IsActive = true
        });
        _context.SaveChanges();
    }

    /// <summary>
    /// Adds a source <see cref="Invoice"/> entity directly to the DB context and saves it.
    /// The invoice has two line items (OrderIndex 1 and 2) with well-known amounts
    /// so copy tests can verify deep-copy correctness without inspecting raw DB state.
    /// </summary>
    /// <param name="documentType">Type of document to seed (default: Invoice)</param>
    /// <param name="status">Status to seed (default: Completed)</param>
    /// <returns>Database-assigned ID of the created invoice</returns>
    private long CreateSourceInvoice(
        EDocumentType documentType = EDocumentType.Invoice,
        EInvoiceStatus status = EInvoiceStatus.Completed)
    {
        var invoice = new Invoice
        {
            DocumentType = documentType,
            Status = status,
            DocumentNumber = "SRC-2026-001",
            VariableSymbol = "2026001",          // must NOT be copied to the new invoice
            ConstantSymbol = "0308",
            SpecificSymbol = "SPEC001",
            ClientId = CustomerId,
            IssuerId = IssuerId,
            CurrencyId = CurrencyId,
            PaymentMethod = EPaymentMethod.BankTransfer,
            BankAccountNumber = "123-456/0800",
            IBAN = "CZ65 0800 0000 0012 3456 7890",
            SWIFT = "GIBACZPX",
            Notes = "Original notes",
            IssueDate = DateTime.UtcNow.AddDays(-10),
            DueDate = DateTime.UtcNow.AddDays(4),
            TotalBeforeVat = 3000m,
            TotalVat = 0m,
            TotalWithVat = 3000m,
            IsSentByEmail = true,
            LastSentByEmailAt = DateTime.UtcNow.AddDays(-5),
            PaidAt = DateTime.UtcNow.AddDays(-1),
            PaidAmount = 3000m,
            InvoiceItem = new List<InvoiceItem>
            {
                new()
                {
                    OrderIndex = 1,
                    Description = "Consulting",
                    Quantity = 10,
                    Unit = "hrs",
                    UnitPrice = 200m,
                    TotalBeforeVat = 2000m,
                    VatRatePercentage = 0,
                    VatAmount = 0,
                    TotalWithVat = 2000m,
                    VatRegime = EVatRegime.Standard
                },
                new()
                {
                    OrderIndex = 2,
                    Description = "Travel",
                    Quantity = 1,
                    Unit = "pcs",
                    UnitPrice = 1000m,
                    TotalBeforeVat = 1000m,
                    VatRatePercentage = 0,
                    VatAmount = 0,
                    TotalWithVat = 1000m,
                    VatRegime = EVatRegime.Standard
                }
            }
        };

        _context.Invoice.Add(invoice);
        _context.SaveChanges();

        return invoice.Id;
    }

    // ═════════════════════════════════════════════════════════════════════════
    // Billing-period shifting
    // ═════════════════════════════════════════════════════════════════════════

    /// <summary>Source issued 2 months ago with period text → copy moves the period 2 months forward.</summary>
    [Fact]
    public async Task CopyInvoiceAsync_ShiftsPeriodsInItemsAndNotes_ByIssueMonthDifference()
    {
        var sourceId = CreateSourceInvoice();
        var source = _context.Invoice.Include(i => i.InvoiceItem).Single(i => i.Id == sourceId);
        source.IssueDate = DateTime.UtcNow.AddMonths(-2);
        source.Notes = "Fakturace za 2026-01";
        source.InvoiceItem.First(i => i.OrderIndex == 1).Description = "Hosting 1/2026";
        _context.SaveChanges();

        var copy = await _service.CopyInvoiceAsync(sourceId);

        var delta = 2;
        delta.ShouldBe(2);
        copy.Notes.ShouldBe(BillingPeriodShifter.Shift("Fakturace za 2026-01", delta));
        copy.Notes.ShouldNotBe("Fakturace za 2026-01");
        copy.InvoiceItem.First(i => i.OrderIndex == 1).Description.ShouldBe(BillingPeriodShifter.Shift("Hosting 1/2026", delta));
        // Untouched text stays as is.
        copy.InvoiceItem.First(i => i.OrderIndex == 2).Description.ShouldBe("Travel");
    }

    /// <summary>shiftPeriods = false keeps the texts verbatim.</summary>
    [Fact]
    public async Task CopyInvoiceAsync_ShiftPeriodsFalse_KeepsTexts()
    {
        var sourceId = CreateSourceInvoice();
        var source = _context.Invoice.Single(i => i.Id == sourceId);
        source.IssueDate = DateTime.UtcNow.AddMonths(-2);
        source.Notes = "Fakturace za 2026-01";
        _context.SaveChanges();

        var copy = await _service.CopyInvoiceAsync(sourceId, shiftPeriods: false);

        copy.Notes.ShouldBe("Fakturace za 2026-01");
    }

    // ═════════════════════════════════════════════════════════════════════════
    // CopyInvoiceAsync — Happy-Path Tests
    // ═════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// Copying a Completed Invoice should produce a new Draft with:
    /// - a fresh DocumentNumber (from number sequence mock)
    /// - a fresh VariableSymbol derived from the new DocumentNumber (NOT the source VS)
    /// </summary>
    [Fact]
    public async Task CopyInvoiceAsync_ShouldCreateDraftWithFreshDocumentNumberAndVs()
    {
        // Arrange — seed a completed invoice that has an existing VS
        var sourceId = CreateSourceInvoice(documentType: EDocumentType.Invoice,
                                           status: EInvoiceStatus.Completed);

        // Act
        var copy = await _service.CopyInvoiceAsync(sourceId);

        // Assert — the copy must be a Draft with the mocked document number
        copy.ShouldNotBeNull();
        copy.Status.ShouldBe(EInvoiceStatus.Draft);
        copy.DocumentNumber.ShouldBe(MockDocumentNumber);

        // VariableSymbol is derived from the new DocumentNumber (digits only, max 10).
        // MockDocumentNumber = "COPY2026001" → digits → "2026001"
        var expectedVs = new string(MockDocumentNumber.Where(char.IsDigit).Take(10).ToArray());
        copy.VariableSymbol.ShouldBe(expectedVs);
    }

    /// <summary>
    /// The copy's VariableSymbol must be derived from its own new DocumentNumber.
    /// The source invoice's VariableSymbol ("2026001") must NOT appear on the copy
    /// when the mock returns a different document number ("COPY2026001").
    /// </summary>
    [Fact]
    public async Task CopyInvoiceAsync_ShouldNotInheritSourceVariableSymbol()
    {
        // Arrange — source has VariableSymbol "2026001"
        var sourceId = CreateSourceInvoice();

        // Change mock to a doc number whose digits produce a clearly different VS
        _numberSequence
            .GenerateNextNumberForDocumentTypeAsync(
                Arg.Any<EDocumentType>(), Arg.Any<DateTime>(),
                Arg.Any<string?>(), Arg.Any<string?>(),
                Arg.Any<CancellationToken>())
            .Returns("INV-9999-999");

        // Act
        var copy = await _service.CopyInvoiceAsync(sourceId);

        // Assert — VS is derived from "INV-9999-999" → "9999999"
        // NOT the source's "2026001"
        copy.VariableSymbol.ShouldBe("9999999");
        copy.VariableSymbol.ShouldNotBe("2026001");  // source VS not inherited
    }

    /// <summary>
    /// Each copied InvoiceItem must be a new entity (no shared IDs with the source).
    /// The item count, descriptions, quantities, and prices must match the source.
    /// </summary>
    [Fact]
    public async Task CopyInvoiceAsync_ShouldDeepCopyItems()
    {
        // Arrange
        var sourceId = CreateSourceInvoice();

        // Act
        var copy = await _service.CopyInvoiceAsync(sourceId);

        // Assert — same number of items as source
        copy.InvoiceItem.ShouldNotBeNull();
        copy.InvoiceItem.Count.ShouldBe(2);

        // Verify item 1 (Consulting)
        var item1 = copy.InvoiceItem.OrderBy(i => i.OrderIndex).First();
        item1.Description.ShouldBe("Consulting");
        item1.Quantity.ShouldBe(10);
        item1.UnitPrice.ShouldBe(200m);
        item1.Unit.ShouldBe("hrs");

        // Verify item 2 (Travel)
        var item2 = copy.InvoiceItem.OrderBy(i => i.OrderIndex).Last();
        item2.Description.ShouldBe("Travel");
        item2.Quantity.ShouldBe(1);
        item2.UnitPrice.ShouldBe(1000m);

        // Items must have new IDs (not shared with source)
        var sourceItems = await _context.InvoiceItem
            .AsNoTracking()
            .Where(i => i.InvoiceId == sourceId)
            .Select(i => i.Id)
            .ToListAsync();

        var copyItemIds = copy.InvoiceItem.Select(i => i.Id).ToList();

        // None of the copied item IDs should match any source item ID
        copyItemIds.Intersect(sourceItems).ShouldBeEmpty(
            "Copied items must be new entities, not shared with the source.");
    }

    /// <summary>
    /// Copying a CreditNote must throw <see cref="InvalidOperationException"/>
    /// because CreditNotes follow their own creation workflow.
    /// The controller maps this to HTTP 400 Bad Request.
    /// </summary>
    [Fact]
    public async Task CopyInvoiceAsync_ShouldRejectCreditNoteSource_BadRequest()
    {
        // Arrange — seed a CreditNote source
        var creditNoteId = CreateSourceInvoice(documentType: EDocumentType.CreditNote);

        // Act & Assert — must throw with a meaningful message
        var ex = await Should.ThrowAsync<InvalidOperationException>(
            () => _service.CopyInvoiceAsync(creditNoteId));

        ex.Message.ShouldContain("CreditNote", Case.Insensitive);
    }

    /// <summary>
    /// The copy must reset all payment and email tracking fields to their defaults,
    /// the same as a freshly created invoice:
    /// - PaidAt = null
    /// - PaidAmount = 0
    /// - IsSentByEmail = false
    /// - LastSentByEmailAt = null
    /// - OriginalInvoiceId = null
    /// </summary>
    [Fact]
    public async Task CopyInvoiceAsync_ShouldResetEmailSentPaidAt()
    {
        // Arrange — source invoice was paid and emailed
        var sourceId = CreateSourceInvoice(status: EInvoiceStatus.Paid);

        // Act
        var copy = await _service.CopyInvoiceAsync(sourceId);

        // Assert — all tracked state fields must be reset
        copy.PaidAt.ShouldBeNull("Copy should not inherit payment date");
        copy.PaidAmount.ShouldBe(0m, "Copy should have zero paid amount");
        copy.IsSentByEmail.ShouldBeFalse("Copy should not inherit email-sent flag");
        copy.LastSentByEmailAt.ShouldBeNull("Copy should not inherit last-sent-by-email date");
        copy.OriginalInvoiceId.ShouldBeNull("Copy is a standalone document — no parent link");
    }

    // ═════════════════════════════════════════════════════════════════════════
    // CopyInvoiceAsync — Additional Correctness Tests
    // ═════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// Copying an invoice that does not exist must throw <see cref="KeyNotFoundException"/>.
    /// The controller maps this to HTTP 404 Not Found.
    /// </summary>
    [Fact]
    public async Task CopyInvoiceAsync_SourceNotFound_ThrowsKeyNotFoundException()
    {
        // Act & Assert
        await Should.ThrowAsync<KeyNotFoundException>(
            () => _service.CopyInvoiceAsync(99999));
    }

    /// <summary>
    /// Copying a Proforma (advance invoice) is allowed.
    /// The copy must preserve the source document type (Proforma stays Proforma).
    /// </summary>
    [Fact]
    public async Task CopyInvoiceAsync_ProformaSource_ShouldCreateDraftProforma()
    {
        // Arrange
        var proformaId = CreateSourceInvoice(documentType: EDocumentType.Proforma,
                                             status: EInvoiceStatus.Paid);

        // Act
        var copy = await _service.CopyInvoiceAsync(proformaId);

        // Assert
        copy.DocumentType.ShouldBe(EDocumentType.Proforma);
        copy.Status.ShouldBe(EInvoiceStatus.Draft);
    }

    /// <summary>
    /// Header fields (bank account, SWIFT, IBAN, ConstantSymbol, SpecificSymbol,
    /// Notes, PaymentMethod, CurrencyId) must be identical on the copy.
    /// </summary>
    [Fact]
    public async Task CopyInvoiceAsync_ShouldCopyHeaderFields()
    {
        // Arrange
        var sourceId = CreateSourceInvoice();
        var source = await _context.Invoice.AsNoTracking()
            .FirstAsync(i => i.Id == sourceId);

        // Act
        var copy = await _service.CopyInvoiceAsync(sourceId);

        // Assert — header fields cloned verbatim
        copy.ClientId.ShouldBe(source.ClientId);
        copy.IssuerId.ShouldBe(source.IssuerId);
        copy.CurrencyId.ShouldBe(source.CurrencyId);
        copy.PaymentMethod.ShouldBe(source.PaymentMethod);
        copy.BankAccountNumber.ShouldBe(source.BankAccountNumber);
        copy.IBAN.ShouldBe(source.IBAN);
        copy.SWIFT.ShouldBe(source.SWIFT);
        copy.ConstantSymbol.ShouldBe(source.ConstantSymbol);
        copy.SpecificSymbol.ShouldBe(source.SpecificSymbol);
        copy.Notes.ShouldBe(source.Notes);
    }

    /// <summary>
    /// The copy's IssueDate must be today (UTC date), not the source's IssueDate.
    /// </summary>
    [Fact]
    public async Task CopyInvoiceAsync_ShouldSetIssueDateToToday()
    {
        // Arrange — source has an older IssueDate
        var sourceId = CreateSourceInvoice();

        // Act
        var copy = await _service.CopyInvoiceAsync(sourceId);

        // Assert — IssueDate is today (UTC)
        copy.IssueDate.ShouldNotBeNull();
        copy.IssueDate!.Value.Date.ShouldBe(DateTime.UtcNow.Date);
    }

    /// <summary>
    /// The copy must be stored as a new distinct row in the database
    /// (different ID from the source).
    /// </summary>
    [Fact]
    public async Task CopyInvoiceAsync_ShouldCreateNewInvoiceRecord()
    {
        // Arrange
        var sourceId = CreateSourceInvoice();

        // Act
        var copy = await _service.CopyInvoiceAsync(sourceId);

        // Assert — IDs must differ; both rows must exist in DB
        copy.Id.ShouldNotBe(sourceId, "Copy must be a new row, not the same entity");

        var invoiceCount = await _context.Invoice
            .CountAsync(i => i.Id == sourceId || i.Id == copy.Id);
        invoiceCount.ShouldBe(2, "Both source and copy must exist in the database");
    }
}

using InvoiceApi.Application.Dto.Invoice;
using InvoiceApi.Application.Service;
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
/// Unit tests for the InvoiceService bulk operations:
/// <see cref="InvoiceService.BulkCompleteAsync"/>,
/// <see cref="InvoiceService.BulkMarkAsPaidAsync"/>, and
/// <see cref="InvoiceService.BulkDeleteAsync"/>.
///
/// Each test uses an isolated InMemoryDatabase (unique name per test class instance)
/// so that test data never leaks between runs. The class implements IDisposable
/// to clean up the DbContext and delete the in-memory database after each test class.
///
/// Mocking strategy:
/// - INumberSequenceService is mocked via NSubstitute — returns "TEST001" for any call.
///   This avoids the need for a real NumberSequence entity in the database.
/// - ILogger is mocked via NSubstitute — we don't assert log calls here, just prevent NREs.
/// </summary>
public class InvoiceServiceBulkTests : IDisposable
{
    // ─── Dependencies ────────────────────────────────────────────────────────

    /// <summary>
    /// The EF Core DbContext backed by an in-memory database.
    /// Each test class instance gets a fresh database (Guid-based name).
    /// </summary>
    private readonly TenantDbContext _context;

    /// <summary>
    /// The service under test — InvoiceService contains the bulk operation methods.
    /// </summary>
    private readonly InvoiceService _service;

    /// <summary>
    /// Mocked logger — prevents null-reference exceptions inside InvoiceService.
    /// We don't verify log calls in these tests; the focus is on business logic.
    /// </summary>
    private readonly ILogger<InvoiceService> _logger;

    /// <summary>
    /// Mocked number sequence service — returns "TEST001" for every call.
    /// The bulk operations themselves don't generate document numbers,
    /// but CompleteInvoiceAsync has a legacy fallback that might call it,
    /// so we configure it to avoid exceptions.
    /// </summary>
    private readonly INumberSequenceService _numberSequence;

    // ─── Well-known IDs for seed data ────────────────────────────────────────

    /// <summary>ID of the customer Client entity (IsIssuer = false).</summary>
    private const long CustomerId = 1;

    /// <summary>ID of the issuer Client entity (IsIssuer = true).</summary>
    private const long IssuerId = 2;

    /// <summary>ID of the seeded Currency entity (CZK).</summary>
    private const long CurrencyId = 1;

    // ─── Constructor & Dispose ───────────────────────────────────────────────

    /// <summary>
    /// Sets up an isolated in-memory database, mocks dependencies,
    /// and seeds the required reference entities (Client, Currency).
    /// Invoice entities are NOT seeded here — each test seeds its own
    /// invoices via the <see cref="AddInvoice"/> helper to keep tests independent.
    /// </summary>
    public InvoiceServiceBulkTests()
    {
        // Create a unique in-memory database for this test class instance.
        // Guid.NewGuid() ensures no data collision between parallel test runs.
        var options = new DbContextOptionsBuilder<TenantDbContext>()
            .UseInMemoryDatabase(databaseName: Guid.NewGuid().ToString())
            .Options;

        _context = new TenantDbContext(options);
        _logger = Substitute.For<ILogger<InvoiceService>>();
        _numberSequence = Substitute.For<INumberSequenceService>();

        // Configure mocked INumberSequenceService to return "TEST001" for any call.
        // BulkComplete delegates to CompleteInvoiceAsync which may call GenerateDocumentNumberAsync
        // as a legacy fallback when DocumentNumber == "DRAFT".
        _numberSequence
            .GenerateNextNumberAsync(
                Arg.Any<long>(), Arg.Any<DateTime>(), Arg.Any<CancellationToken>())
            .Returns("TEST001");

        _numberSequence
            .GenerateNextNumberForDocumentTypeAsync(
                Arg.Any<EDocumentType>(), Arg.Any<DateTime>(),
                Arg.Any<string?>(), Arg.Any<string?>(),
                Arg.Any<CancellationToken>())
            .Returns("TEST001");

        // Instantiate the service under test with all dependencies.
        _service = new InvoiceService(_context, _numberSequence, _logger);

        // Seed reference entities that every test needs.
        SeedReferenceData();
    }

    /// <summary>
    /// Cleans up the in-memory database and disposes the DbContext.
    /// Called automatically by xUnit after all tests in the class finish.
    /// </summary>
    public void Dispose()
    {
        _context.Database.EnsureDeleted();
        _context.Dispose();
    }

    // ─── Seed Helpers ────────────────────────────────────────────────────────

    /// <summary>
    /// Seeds the minimum reference entities required by Invoice foreign keys:
    /// - A customer Client (IsIssuer = false) with RegistrationNumber set.
    /// - An issuer Client (IsIssuer = true) with RegistrationNumber set.
    /// - A Currency (CZK) with Code, Symbol, Name, IsActive.
    ///
    /// Each entity is saved individually because InMemoryDatabase
    /// resolves FK constraints eagerly and requires referenced entities
    /// to already exist before dependents are added.
    /// </summary>
    private void SeedReferenceData()
    {
        // Customer — the party being invoiced
        _context.Client.Add(new Client
        {
            Id = CustomerId,
            CompanyName = "Test Customer",
            RegistrationNumber = "CUST-001",  // Required by DbContext config
            IsIssuer = false,
            IsActive = true
        });
        _context.SaveChanges();

        // Issuer — the company issuing the invoice
        _context.Client.Add(new Client
        {
            Id = IssuerId,
            CompanyName = "Test Issuer Ltd.",
            RegistrationNumber = "ISS-001",  // Required by DbContext config
            IsIssuer = true,
            IsActive = true,
            IsVatPayer = false  // Non-VAT payer keeps tests simpler (no VatRate needed)
        });
        _context.SaveChanges();

        // Currency — CZK (Czech Koruna), required FK for every Invoice
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
    }

    /// <summary>
    /// Helper method to add a single Invoice entity to the database
    /// with all required fields properly set. Each invoice is saved
    /// individually (InMemoryDb pattern) and gets an empty InvoiceItem list.
    ///
    /// <para>
    /// <b>Why set all these fields?</b>
    /// InMemoryDatabase enforces <c>IsRequired()</c> constraints from the
    /// Fluent API configuration. Missing required fields cause SaveChanges to fail.
    /// </para>
    /// </summary>
    /// <param name="documentNumber">
    /// The document number displayed on the invoice (e.g., "INV-001").
    /// Must be unique per invoice for clarity in test assertions.
    /// </param>
    /// <param name="status">
    /// The initial status of the invoice (Draft, Completed, Paid, etc.).
    /// This determines which bulk operations are valid for this invoice.
    /// </param>
    /// <returns>The database-generated ID of the newly created invoice.</returns>
    private long AddInvoice(string documentNumber, EInvoiceStatus status)
    {
        var invoice = new Invoice
        {
            DocumentNumber = documentNumber,
            DocumentType = EDocumentType.Invoice,
            Status = status,
            ClientId = CustomerId,
            IssuerId = IssuerId,
            CurrencyId = CurrencyId,
            TotalWithVat = 1000m,
            IssueDate = DateTime.UtcNow,
            InvoiceItem = new List<InvoiceItem>()  // Required: prevents null reference in queries
        };

        _context.Invoice.Add(invoice);
        _context.SaveChanges();

        return invoice.Id;
    }

    // ═════════════════════════════════════════════════════════════════════════
    // BulkCompleteAsync Tests
    // ═════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// When all provided invoice IDs are in Draft status,
    /// BulkCompleteAsync should transition every one of them to Completed
    /// and report SuccessCount = 3, FailedCount = 0, no errors.
    /// </summary>
    [Fact]
    public async Task BulkComplete_AllDraft_ShouldCompleteAll()
    {
        // Arrange — create 3 draft invoices
        var id1 = AddInvoice("BULK-C-001", EInvoiceStatus.Draft);
        var id2 = AddInvoice("BULK-C-002", EInvoiceStatus.Draft);
        var id3 = AddInvoice("BULK-C-003", EInvoiceStatus.Draft);
        var ids = new List<long> { id1, id2, id3 };

        // Act — bulk complete all 3 drafts
        var result = await _service.BulkCompleteAsync(ids);

        // Assert — all 3 should succeed with zero failures
        result.ShouldNotBeNull();
        result.SuccessCount.ShouldBe(3);
        result.FailedCount.ShouldBe(0);
        result.Errors.ShouldBeEmpty();

        // Verify each invoice is now in Completed status in the database.
        // We use AsNoTracking to get a fresh read (not cached by change tracker).
        foreach (var id in ids)
        {
            var invoice = await _context.Invoice.AsNoTracking()
                .FirstOrDefaultAsync(i => i.Id == id);
            invoice.ShouldNotBeNull();
            invoice.Status.ShouldBe(EInvoiceStatus.Completed);
        }
    }

    /// <summary>
    /// When the input list contains invoices in mixed statuses (Draft + Completed + Paid),
    /// only the Draft invoice should be completed successfully.
    /// The already-Completed and Paid invoices should fail with error messages
    /// because CompleteInvoiceAsync throws for non-Draft statuses.
    /// </summary>
    [Fact]
    public async Task BulkComplete_MixedStatuses_ShouldOnlyCompleteDrafts()
    {
        // Arrange — 1 draft, 1 already completed, 1 already paid
        var draftId = AddInvoice("BULK-MIX-DRAFT", EInvoiceStatus.Draft);
        var completedId = AddInvoice("BULK-MIX-COMPLETED", EInvoiceStatus.Completed);
        var paidId = AddInvoice("BULK-MIX-PAID", EInvoiceStatus.Paid);
        var ids = new List<long> { draftId, completedId, paidId };

        // Act — attempt to complete all 3
        var result = await _service.BulkCompleteAsync(ids);

        // Assert — only the draft should succeed; the other 2 should fail
        result.SuccessCount.ShouldBe(1);
        result.FailedCount.ShouldBe(2);
        result.Errors.Count.ShouldBe(2);

        // Verify the draft invoice is now Completed
        var draftInvoice = await _context.Invoice.AsNoTracking()
            .FirstOrDefaultAsync(i => i.Id == draftId);
        draftInvoice.ShouldNotBeNull();
        draftInvoice.Status.ShouldBe(EInvoiceStatus.Completed);

        // Verify the error list contains entries for the non-draft invoices
        result.Errors.ShouldContain(e => e.InvoiceId == completedId);
        result.Errors.ShouldContain(e => e.InvoiceId == paidId);
    }

    /// <summary>
    /// When the input list contains IDs that don't exist in the database,
    /// BulkCompleteAsync should report them in the errors list
    /// (CompleteInvoiceAsync returns null for non-existent IDs).
    /// </summary>
    [Fact]
    public async Task BulkComplete_NonExistentIds_ShouldReportErrors()
    {
        // Arrange — use IDs that definitely don't exist
        var nonExistentIds = new List<long> { 99990, 99991, 99992 };

        // Act
        var result = await _service.BulkCompleteAsync(nonExistentIds);

        // Assert — all 3 should fail because the invoices don't exist
        result.SuccessCount.ShouldBe(0);
        result.FailedCount.ShouldBe(3);
        result.Errors.Count.ShouldBe(3);

        // Each error should reference the correct invoice ID
        result.Errors.ShouldContain(e => e.InvoiceId == 99990);
        result.Errors.ShouldContain(e => e.InvoiceId == 99991);
        result.Errors.ShouldContain(e => e.InvoiceId == 99992);

        // Each error should have a non-empty error message
        result.Errors.ShouldAllBe(e => !string.IsNullOrWhiteSpace(e.Error));
    }

    // ═════════════════════════════════════════════════════════════════════════
    // BulkMarkAsPaidAsync Tests
    // ═════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// When all provided invoice IDs are in Completed status,
    /// BulkMarkAsPaidAsync should transition every one of them to Paid
    /// and report SuccessCount = 3, FailedCount = 0.
    /// </summary>
    [Fact]
    public async Task BulkMarkAsPaid_AllCompleted_ShouldMarkAll()
    {
        // Arrange — create 3 completed invoices (only Completed can be marked as paid)
        var id1 = AddInvoice("BULK-P-001", EInvoiceStatus.Completed);
        var id2 = AddInvoice("BULK-P-002", EInvoiceStatus.Completed);
        var id3 = AddInvoice("BULK-P-003", EInvoiceStatus.Completed);
        var ids = new List<long> { id1, id2, id3 };

        // Act — bulk mark all 3 as paid
        var result = await _service.BulkMarkAsPaidAsync(ids);

        // Assert — all 3 should succeed with zero failures
        result.ShouldNotBeNull();
        result.SuccessCount.ShouldBe(3);
        result.FailedCount.ShouldBe(0);
        result.Errors.ShouldBeEmpty();

        // Verify each invoice is now in Paid status in the database
        foreach (var id in ids)
        {
            var invoice = await _context.Invoice.AsNoTracking()
                .FirstOrDefaultAsync(i => i.Id == id);
            invoice.ShouldNotBeNull();
            invoice.Status.ShouldBe(EInvoiceStatus.Paid);
            invoice.PaidAt.ShouldNotBeNull();  // PaidAt should be set
        }
    }

    /// <summary>
    /// When a Draft invoice is included in a BulkMarkAsPaid request,
    /// it should fail because MarkAsPaidAsync requires Completed status.
    /// The error message should indicate "Only completed invoices can be marked as paid".
    /// </summary>
    [Fact]
    public async Task BulkMarkAsPaid_DraftInvoice_ShouldFail()
    {
        // Arrange — create a draft invoice (cannot be marked as paid directly)
        var draftId = AddInvoice("BULK-P-DRAFT", EInvoiceStatus.Draft);
        var ids = new List<long> { draftId };

        // Act — attempt to mark a draft as paid
        var result = await _service.BulkMarkAsPaidAsync(ids);

        // Assert — should fail with 1 error
        result.SuccessCount.ShouldBe(0);
        result.FailedCount.ShouldBe(1);
        result.Errors.Count.ShouldBe(1);

        // Verify the error references the correct invoice and explains why it failed
        var error = result.Errors.First();
        error.InvoiceId.ShouldBe(draftId);
        error.Error.ShouldContain("Only completed invoices can be marked as paid");

        // Verify the invoice status is unchanged (still Draft)
        var invoice = await _context.Invoice.AsNoTracking()
            .FirstOrDefaultAsync(i => i.Id == draftId);
        invoice.ShouldNotBeNull();
        invoice.Status.ShouldBe(EInvoiceStatus.Draft);
    }

    // ═════════════════════════════════════════════════════════════════════════
    // BulkDeleteAsync Tests
    // ═════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// When all provided invoice IDs are in Draft status,
    /// BulkDeleteAsync should soft-delete every one of them
    /// (set Status = Deleted) and report SuccessCount = 3, FailedCount = 0.
    /// </summary>
    [Fact]
    public async Task BulkDelete_AllDraft_ShouldDeleteAll()
    {
        // Arrange — create 3 draft invoices (only Draft can be deleted)
        var id1 = AddInvoice("BULK-D-001", EInvoiceStatus.Draft);
        var id2 = AddInvoice("BULK-D-002", EInvoiceStatus.Draft);
        var id3 = AddInvoice("BULK-D-003", EInvoiceStatus.Draft);
        var ids = new List<long> { id1, id2, id3 };

        // Act — bulk delete all 3 drafts
        var result = await _service.BulkDeleteAsync(ids);

        // Assert — all 3 should succeed with zero failures
        result.ShouldNotBeNull();
        result.SuccessCount.ShouldBe(3);
        result.FailedCount.ShouldBe(0);
        result.Errors.ShouldBeEmpty();

        // Verify each invoice is now soft-deleted (Status = Deleted) in the database.
        // The entity still exists — it's a soft delete, not a hard delete.
        foreach (var id in ids)
        {
            var invoice = await _context.Invoice.AsNoTracking()
                .FirstOrDefaultAsync(i => i.Id == id);
            invoice.ShouldNotBeNull();  // Entity still exists in DB
            invoice.Status.ShouldBe(EInvoiceStatus.Deleted);  // But is marked as deleted
        }
    }

    /// <summary>
    /// When a Completed invoice is included in a BulkDelete request,
    /// it should fail because DeleteInvoiceAsync only allows Draft invoices.
    /// The error message should indicate "Only draft invoices can be deleted".
    /// </summary>
    [Fact]
    public async Task BulkDelete_CompletedInvoice_ShouldFail()
    {
        // Arrange — create a completed invoice (cannot be deleted)
        var completedId = AddInvoice("BULK-D-COMPLETED", EInvoiceStatus.Completed);
        var ids = new List<long> { completedId };

        // Act — attempt to delete a completed invoice
        var result = await _service.BulkDeleteAsync(ids);

        // Assert — should fail with 1 error
        result.SuccessCount.ShouldBe(0);
        result.FailedCount.ShouldBe(1);
        result.Errors.Count.ShouldBe(1);

        // Verify the error references the correct invoice and explains why it failed
        var error = result.Errors.First();
        error.InvoiceId.ShouldBe(completedId);
        error.Error.ShouldContain("Only draft invoices can be deleted");

        // Verify the invoice status is unchanged (still Completed)
        var invoice = await _context.Invoice.AsNoTracking()
            .FirstOrDefaultAsync(i => i.Id == completedId);
        invoice.ShouldNotBeNull();
        invoice.Status.ShouldBe(EInvoiceStatus.Completed);
    }

    /// <summary>
    /// When an empty list is passed to BulkDeleteAsync,
    /// it should return immediately with SuccessCount = 0, FailedCount = 0,
    /// and an empty errors list. This is an edge case / no-op scenario.
    /// </summary>
    [Fact]
    public async Task BulkDelete_EmptyList_ShouldReturnZeroCounts()
    {
        // Arrange — empty list of invoice IDs
        var ids = new List<long>();

        // Act — bulk delete with no IDs
        var result = await _service.BulkDeleteAsync(ids);

        // Assert — zero success, zero failures, no errors
        result.ShouldNotBeNull();
        result.SuccessCount.ShouldBe(0);
        result.FailedCount.ShouldBe(0);
        result.Errors.ShouldBeEmpty();
    }
}

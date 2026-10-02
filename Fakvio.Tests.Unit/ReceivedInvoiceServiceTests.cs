using Fakvio.Contracts.Dto.ReceivedInvoice;
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
/// Unit tests for ReceivedInvoiceService — covers CRUD operations,
/// status transitions (Received → Approved → Paid, Reject, Delete),
/// totals calculation, and validation rules.
/// Uses InMemoryDatabase for isolation.
/// </summary>
public class ReceivedInvoiceServiceTests : IDisposable
{
    private readonly TenantDbContext _context;
    private readonly ReceivedInvoiceService _service;

    public ReceivedInvoiceServiceTests()
    {
        var options = new DbContextOptionsBuilder<TenantDbContext>()
            .UseInMemoryDatabase(databaseName: Guid.NewGuid().ToString())
            .Options;

        _context = new TenantDbContext(options);
        var logger = Substitute.For<ILogger<ReceivedInvoiceService>>();
        _service = new ReceivedInvoiceService(_context, logger);

        SeedTestData();
    }

    public void Dispose()
    {
        _context.Database.EnsureDeleted();
        _context.Dispose();
    }

    /// <summary>
    /// Seeds supplier, currency, and VAT rate for test scenarios.
    /// </summary>
    private void SeedTestData()
    {
        _context.Client.Add(new Client
        {
            Id = 1, CompanyName = "Supplier A", RegistrationNumber = "SUP001",
            IsIssuer = false, IsActive = true
        });
        _context.SaveChanges();

        _context.Client.Add(new Client
        {
            Id = 2, CompanyName = "Inactive Supplier", RegistrationNumber = "SUP002",
            IsIssuer = false, IsActive = false
        });
        _context.SaveChanges();

        _context.Currency.Add(new Currency
        {
            Id = 1, Code = "CZK", Name = "Czech Koruna", Symbol = "Kč",
            DecimalPlaces = 2, SortOrder = 1, IsActive = true
        });
        _context.SaveChanges();

        _context.VatRate.Add(new VatRate
        {
            Id = 1, Name = "Standard 21%", Rate = 21, IsDefault = true,
            ValidFrom = DateTime.UtcNow.AddYears(-1), IsActive = true
        });
        _context.SaveChanges();
    }

    /// <summary>
    /// Helper: creates a valid CreateReceivedInvoiceDto with one item.
    /// </summary>
    private CreateReceivedInvoiceDto CreateValidDto()
    {
        return new CreateReceivedInvoiceDto
        {
            DocumentNumber = "FAK-2026-001",
            SupplierId = 1,
            CurrencyId = 1,
            IssueDate = new DateTime(2026, 3, 1, 0, 0, 0, DateTimeKind.Utc),
            DueDate = new DateTime(2026, 3, 31, 0, 0, 0, DateTimeKind.Utc),
            VariableSymbol = "2026001",
            Items = new List<CreateReceivedInvoiceItemDto>
            {
                new()
                {
                    OrderIndex = 1,
                    Description = "Office supplies",
                    Quantity = 10,
                    UnitPrice = 100,
                    VatRatePercentage = 21,
                    Unit = "pcs"
                }
            }
        };
    }

    /// <summary>
    /// Helper: creates a received invoice in DB and returns its ID.
    /// </summary>
    private async Task<long> CreateInvoiceInDb(EReceivedInvoiceStatus? overrideStatus = null)
    {
        var result = await _service.CreateAsync(CreateValidDto());
        if (overrideStatus.HasValue && overrideStatus != EReceivedInvoiceStatus.Received)
        {
            var entity = await _context.ReceivedInvoice.FindAsync(result.Id);
            entity!.Status = overrideStatus.Value;
            await _context.SaveChangesAsync();
        }
        return result.Id;
    }

    // ── Create Tests ─────────────────────────────────────────────────

    [Fact]
    public async Task CreateAsync_ValidDto_ReturnsInvoiceWithCorrectTotals()
    {
        var dto = CreateValidDto();

        var result = await _service.CreateAsync(dto);

        result.ShouldNotBeNull();
        result.DocumentNumber.ShouldBe("FAK-2026-001");
        result.Status.ShouldBe(EReceivedInvoiceStatus.Received);
        result.SupplierId.ShouldBe(1);
        // 10 * 100 = 1000 before VAT, 1000 * 0.21 = 210 VAT
        result.TotalBeforeVat.ShouldBe(1000m);
        result.TotalVat.ShouldBe(210m);
        result.TotalWithVat.ShouldBe(1210m);
    }

    [Fact]
    public async Task CreateAsync_WithVatRateId_UsesRateFromDatabase()
    {
        var dto = CreateValidDto();
        dto.Items[0].VatRateId = 1; // 21% from seed
        dto.Items[0].VatRatePercentage = 0; // Should be overridden by DB rate

        var result = await _service.CreateAsync(dto);

        result.TotalVat.ShouldBe(210m); // 1000 * 0.21
    }

    [Fact]
    public async Task CreateAsync_InactiveSupplier_ThrowsInvalidOperation()
    {
        var dto = CreateValidDto();
        dto.SupplierId = 2; // Inactive supplier

        await Should.ThrowAsync<InvalidOperationException>(
            () => _service.CreateAsync(dto));
    }

    [Fact]
    public async Task CreateAsync_NonExistentSupplier_ThrowsInvalidOperation()
    {
        var dto = CreateValidDto();
        dto.SupplierId = 999;

        await Should.ThrowAsync<InvalidOperationException>(
            () => _service.CreateAsync(dto));
    }

    [Fact]
    public async Task CreateAsync_MultipleItems_CalculatesTotalsCorrectly()
    {
        var dto = CreateValidDto();
        dto.Items.Add(new CreateReceivedInvoiceItemDto
        {
            OrderIndex = 2,
            Description = "Consulting",
            Quantity = 5,
            UnitPrice = 200,
            VatRatePercentage = 21,
            Unit = "hrs"
        });

        var result = await _service.CreateAsync(dto);

        // Item 1: 10*100 = 1000, VAT = 210
        // Item 2: 5*200 = 1000, VAT = 210
        result.TotalBeforeVat.ShouldBe(2000m);
        result.TotalVat.ShouldBe(420m);
        result.TotalWithVat.ShouldBe(2420m);
    }

    // ── Read Tests ───────────────────────────────────────────────────

    [Fact]
    public async Task GetByIdAsync_ExistingId_ReturnsInvoice()
    {
        var id = await CreateInvoiceInDb();

        var result = await _service.GetByIdAsync(id);

        result.ShouldNotBeNull();
        result.Id.ShouldBe(id);
        result.SupplierName.ShouldBe("Supplier A");
        result.CurrencyCode.ShouldBe("CZK");
    }

    [Fact]
    public async Task GetByIdAsync_NonExistentId_ReturnsNull()
    {
        var result = await _service.GetByIdAsync(999);

        result.ShouldBeNull();
    }

    [Fact]
    public async Task GetAllAsync_ExcludesDeletedInvoices()
    {
        await CreateInvoiceInDb();
        await CreateInvoiceInDb(EReceivedInvoiceStatus.Deleted);

        var result = await _service.GetAllAsync();

        result.Count.ShouldBe(1);
    }

    [Fact]
    public async Task GetAllAsync_FilterByStatus_ReturnsMatching()
    {
        await CreateInvoiceInDb(); // Status = Received
        var approvedId = await CreateInvoiceInDb();
        await _service.ApproveAsync(approvedId);

        var result = await _service.GetAllAsync(status: EReceivedInvoiceStatus.Approved);

        result.Count.ShouldBe(1);
        result[0].Status.ShouldBe(EReceivedInvoiceStatus.Approved);
    }

    [Fact]
    public async Task GetAllAsync_FilterBySupplierId_ReturnsMatching()
    {
        await CreateInvoiceInDb();

        var result = await _service.GetAllAsync(supplierId: 1);
        result.Count.ShouldBe(1);

        var empty = await _service.GetAllAsync(supplierId: 999);
        empty.Count.ShouldBe(0);
    }

    // ── Status Transition Tests ──────────────────────────────────────

    [Fact]
    public async Task ApproveAsync_ReceivedInvoice_ChangesStatusToApproved()
    {
        var id = await CreateInvoiceInDb();

        var result = await _service.ApproveAsync(id);

        result.ShouldNotBeNull();
        result!.Status.ShouldBe(EReceivedInvoiceStatus.Approved);
    }

    [Fact]
    public async Task ApproveAsync_AlreadyApproved_ThrowsInvalidOperation()
    {
        var id = await CreateInvoiceInDb();
        await _service.ApproveAsync(id);

        await Should.ThrowAsync<InvalidOperationException>(
            () => _service.ApproveAsync(id));
    }

    [Fact]
    public async Task ApproveAsync_NonExistentId_ReturnsNull()
    {
        var result = await _service.ApproveAsync(999);
        result.ShouldBeNull();
    }

    [Fact]
    public async Task MarkAsPaidAsync_ApprovedInvoice_ChangesStatusToPaid()
    {
        var id = await CreateInvoiceInDb();
        await _service.ApproveAsync(id);

        var paidDate = new DateTime(2026, 3, 15, 0, 0, 0, DateTimeKind.Utc);
        var result = await _service.MarkAsPaidAsync(id, paidDate);

        result.ShouldNotBeNull();
        result!.Status.ShouldBe(EReceivedInvoiceStatus.Paid);
        result.PaidAt.ShouldNotBeNull();
    }

    [Fact]
    public async Task MarkAsPaidAsync_ReceivedInvoice_ThrowsInvalidOperation()
    {
        var id = await CreateInvoiceInDb(); // Status = Received

        await Should.ThrowAsync<InvalidOperationException>(
            () => _service.MarkAsPaidAsync(id));
    }

    [Fact]
    public async Task RejectAsync_ReceivedInvoice_ChangesStatusToRejected()
    {
        var id = await CreateInvoiceInDb();

        var result = await _service.RejectAsync(id);

        result.ShouldNotBeNull();
        result!.Status.ShouldBe(EReceivedInvoiceStatus.Rejected);
    }

    [Fact]
    public async Task RejectAsync_ApprovedInvoice_ThrowsInvalidOperation()
    {
        var id = await CreateInvoiceInDb();
        await _service.ApproveAsync(id);

        await Should.ThrowAsync<InvalidOperationException>(
            () => _service.RejectAsync(id));
    }

    [Fact]
    public async Task DeleteAsync_ReceivedInvoice_SoftDeletes()
    {
        var id = await CreateInvoiceInDb();

        var result = await _service.DeleteAsync(id);

        result.ShouldBeTrue();
        var entity = await _context.ReceivedInvoice.FindAsync(id);
        entity!.Status.ShouldBe(EReceivedInvoiceStatus.Deleted);
    }

    [Fact]
    public async Task DeleteAsync_RejectedInvoice_SoftDeletes()
    {
        var id = await CreateInvoiceInDb();
        await _service.RejectAsync(id);

        var result = await _service.DeleteAsync(id);

        result.ShouldBeTrue();
    }

    [Fact]
    public async Task DeleteAsync_ApprovedInvoice_ThrowsInvalidOperation()
    {
        var id = await CreateInvoiceInDb();
        await _service.ApproveAsync(id);

        await Should.ThrowAsync<InvalidOperationException>(
            () => _service.DeleteAsync(id));
    }

    [Fact]
    public async Task DeleteAsync_PaidInvoice_ThrowsInvalidOperation()
    {
        var id = await CreateInvoiceInDb();
        await _service.ApproveAsync(id);
        await _service.MarkAsPaidAsync(id);

        await Should.ThrowAsync<InvalidOperationException>(
            () => _service.DeleteAsync(id));
    }

    [Fact]
    public async Task DeleteAsync_NonExistentId_ReturnsFalse()
    {
        var result = await _service.DeleteAsync(999);
        result.ShouldBeFalse();
    }

    // ── Update Tests ─────────────────────────────────────────────────

    [Fact]
    public async Task UpdateAsync_ValidUpdate_ChangesFields()
    {
        var id = await CreateInvoiceInDb();

        var updateDto = new UpdateReceivedInvoiceDto
        {
            DocumentNumber = "FAK-2026-UPDATED",
            Notes = "Updated notes"
        };

        var result = await _service.UpdateAsync(id, updateDto);

        result.ShouldNotBeNull();
        result!.DocumentNumber.ShouldBe("FAK-2026-UPDATED");
        result.Notes.ShouldBe("Updated notes");
    }

    [Fact]
    public async Task UpdateAsync_PaidInvoice_ThrowsInvalidOperation()
    {
        var id = await CreateInvoiceInDb();
        await _service.ApproveAsync(id);
        await _service.MarkAsPaidAsync(id);

        var updateDto = new UpdateReceivedInvoiceDto { Notes = "Should fail" };

        await Should.ThrowAsync<InvalidOperationException>(
            () => _service.UpdateAsync(id, updateDto));
    }

    [Fact]
    public async Task UpdateAsync_WithNewItems_RecalculatesTotals()
    {
        var id = await CreateInvoiceInDb();

        var updateDto = new UpdateReceivedInvoiceDto
        {
            Items = new List<CreateReceivedInvoiceItemDto>
            {
                new()
                {
                    OrderIndex = 1,
                    Description = "New item",
                    Quantity = 2,
                    UnitPrice = 500,
                    VatRatePercentage = 21,
                    Unit = "pcs"
                }
            }
        };

        var result = await _service.UpdateAsync(id, updateDto);

        // 2 * 500 = 1000, VAT = 210
        result.ShouldNotBeNull();
        result!.TotalBeforeVat.ShouldBe(1000m);
        result.TotalVat.ShouldBe(210m);
        result.TotalWithVat.ShouldBe(1210m);
    }

    [Fact]
    public async Task UpdateAsync_NonExistentId_ReturnsNull()
    {
        var result = await _service.UpdateAsync(999, new UpdateReceivedInvoiceDto());
        result.ShouldBeNull();
    }

    // ── GetPagedAsync — column filters & sorting ─────────────────────

    /// <summary>
    /// Helper: creates a second active supplier and one invoice per supplier
    /// with distinct document numbers, so filter tests can distinguish rows.
    /// </summary>
    private async Task SeedTwoInvoicesForFiltering()
    {
        _context.Client.Add(new Client
        {
            Id = 3, CompanyName = "Supplier B", RegistrationNumber = "SUP003",
            IsIssuer = false, IsActive = true
        });
        await _context.SaveChangesAsync();

        var first = CreateValidDto(); // DocumentNumber FAK-2026-001, SupplierId 1 (Supplier A)
        await _service.CreateAsync(first);

        var second = CreateValidDto();
        second.DocumentNumber = "INV-2026-777";
        second.SupplierId = 3;
        await _service.CreateAsync(second);
    }

    [Fact]
    public async Task GetPagedAsync_DocumentNumberFilter_ReturnsOnlyMatchingRows()
    {
        await SeedTwoInvoicesForFiltering();

        // Case-insensitive contains — "inv-2026" must match only "INV-2026-777"
        var result = await _service.GetPagedAsync(new ReceivedInvoiceFilterDto
        {
            DocumentNumber = "inv-2026"
        });

        result.Items.Count.ShouldBe(1);
        result.Items[0].DocumentNumber.ShouldBe("INV-2026-777");
    }

    [Fact]
    public async Task GetPagedAsync_SupplierNameFilter_ReturnsOnlyMatchingRows()
    {
        await SeedTwoInvoicesForFiltering();

        var result = await _service.GetPagedAsync(new ReceivedInvoiceFilterDto
        {
            SupplierName = "supplier b"
        });

        result.Items.Count.ShouldBe(1);
        result.Items[0].SupplierName.ShouldBe("Supplier B");
    }

    [Fact]
    public async Task GetPagedAsync_DocumentNumberFilter_NoMatch_ReturnsEmpty()
    {
        await SeedTwoInvoicesForFiltering();

        var result = await _service.GetPagedAsync(new ReceivedInvoiceFilterDto
        {
            DocumentNumber = "does-not-exist"
        });

        result.Items.ShouldBeEmpty();
        result.TotalCount.ShouldBe(0);
    }

    [Fact]
    public async Task GetPagedAsync_SortByDocumentNumberAscending_OrdersCorrectly()
    {
        await SeedTwoInvoicesForFiltering();

        var result = await _service.GetPagedAsync(new ReceivedInvoiceFilterDto
        {
            SortBy = "documentnumber",
            SortDirection = "asc"
        });

        result.Items.Count.ShouldBe(2);
        result.Items[0].DocumentNumber.ShouldBe("FAK-2026-001");
        result.Items[1].DocumentNumber.ShouldBe("INV-2026-777");
    }

    [Fact]
    public async Task GetPagedAsync_UnknownSortField_FallsBackToDefaultWithoutError()
    {
        await SeedTwoInvoicesForFiltering();

        // Garbage sort field must not throw — falls back to ReceivedDate ordering
        var result = await _service.GetPagedAsync(new ReceivedInvoiceFilterDto
        {
            SortBy = "nonsense-field"
        });

        result.Items.Count.ShouldBe(2);
    }

    // ── GetPagedAsync — AttachmentCount ──────────────────────────────

    /// <summary>
    /// Helper: adds a FileAttachment metadata row for the given entity record.
    /// Only metadata matters for AttachmentCount — no blob storage involved.
    /// </summary>
    private void AddAttachment(string entityName, long recordId, string fileName)
    {
        _context.FileAttachment.Add(new FileAttachment
        {
            EntityName = entityName,
            RecordId = recordId,
            OriginalFileName = fileName,
            ContentType = "application/pdf",
            FileSizeBytes = 100,
            BlobPath = $"1/{Guid.NewGuid()}.pdf"
        });
        _context.SaveChanges();
    }

    [Fact]
    public async Task GetPagedAsync_AttachmentCount_ReflectsPerInvoiceCounts()
    {
        var firstId = await CreateInvoiceInDb();

        var second = CreateValidDto();
        second.DocumentNumber = "FAK-2026-002";
        var secondId = (await _service.CreateAsync(second)).Id;

        AddAttachment(nameof(ReceivedInvoice), firstId, "scan.pdf");
        AddAttachment(nameof(ReceivedInvoice), firstId, "photo.jpg");
        // secondId gets no attachments

        var result = await _service.GetPagedAsync(new ReceivedInvoiceFilterDto());

        result.Items.Single(i => i.Id == firstId).AttachmentCount.ShouldBe(2);
        result.Items.Single(i => i.Id == secondId).AttachmentCount.ShouldBe(0);
    }

    [Fact]
    public async Task GetPagedAsync_AttachmentCount_IgnoresOtherEntityTypes()
    {
        var id = await CreateInvoiceInDb();

        // Same RecordId but different EntityName — must NOT be counted
        AddAttachment("Invoice", id, "unrelated.pdf");
        AddAttachment(nameof(ReceivedInvoice), id, "mine.pdf");

        var result = await _service.GetPagedAsync(new ReceivedInvoiceFilterDto());

        result.Items.Single(i => i.Id == id).AttachmentCount.ShouldBe(1);
    }

    // ── Full Lifecycle Test ──────────────────────────────────────────

    [Fact]
    public async Task FullLifecycle_Received_Approved_Paid()
    {
        // Create
        var created = await _service.CreateAsync(CreateValidDto());
        created.Status.ShouldBe(EReceivedInvoiceStatus.Received);

        // Approve
        var approved = await _service.ApproveAsync(created.Id);
        approved!.Status.ShouldBe(EReceivedInvoiceStatus.Approved);

        // Mark as paid
        var paid = await _service.MarkAsPaidAsync(created.Id);
        paid!.Status.ShouldBe(EReceivedInvoiceStatus.Paid);
        paid.PaidAt.ShouldNotBeNull();
    }

    // ── Reverse charge (PDP) ─────────────────────────────────────────

    [Fact]
    public async Task CreateAsync_ReverseChargeItem_SelfAssessesVat_SupplierBillsNone()
    {
        _context.ReverseChargeCode.Add(new ReverseChargeCode { Id = 5, Code = "4", NameCs = "Stavebni prace", ParagraphRef = "92e" });
        _context.SaveChanges();
        var dto = CreateValidDto();
        dto.Items[0].VatRegime = EVatRegime.ReverseCharge;
        dto.Items[0].ReverseChargeCodeId = 5;

        var result = await _service.CreateAsync(dto);

        // 1000 base: we pay 1000 (no VAT billed), but must self-assess 210.
        result.TotalVat.ShouldBe(0m);
        result.TotalWithVat.ShouldBe(1000m);
        result.Items[0].InformationalVatAmount.ShouldBe(210m);
        result.Items[0].VatRegime.ShouldBe(EVatRegime.ReverseCharge);
    }

    [Fact]
    public async Task CreateAsync_ReverseChargeWithoutCode_Throws()
    {
        var dto = CreateValidDto();
        dto.Items[0].VatRegime = EVatRegime.ReverseCharge;

        await Should.ThrowAsync<InvalidOperationException>(() => _service.CreateAsync(dto));
    }

    [Fact]
    public async Task CreateAsync_CodeOnStandardItem_Throws()
    {
        var dto = CreateValidDto();
        dto.Items[0].ReverseChargeCodeId = 5;

        await Should.ThrowAsync<InvalidOperationException>(() => _service.CreateAsync(dto));
    }

    [Fact]
    public async Task CreateAsync_ReverseChargeWithZeroRate_Throws()
    {
        _context.ReverseChargeCode.Add(new ReverseChargeCode { Id = 6, Code = "4", NameCs = "x", ParagraphRef = "92e" });
        _context.SaveChanges();
        var dto = CreateValidDto();
        dto.Items[0].VatRegime = EVatRegime.ReverseCharge;
        dto.Items[0].ReverseChargeCodeId = 6;
        dto.Items[0].VatRatePercentage = 0;

        await Should.ThrowAsync<InvalidOperationException>(() => _service.CreateAsync(dto));
    }
}

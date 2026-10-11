using Fakvio.Application.Service;
using Fakvio.Contracts.Dto.Invoice;
using Fakvio.Contracts.Dto.ReceivedInvoice;
using Fakvio.Domain.Entities;
using Fakvio.Domain.Enums;
using Fakvio.Infrastructure.Data;
using Fakvio.Infrastructure.Service;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Shouldly;

namespace Fakvio.Tests.Unit;

/// <summary>
/// Contract of the UNIFIED create / update for a RECEIVED document (<see cref="EDocumentType.ReceivedInvoice"/>
/// through <see cref="InvoiceService"/>) and its equivalence with the legacy <see cref="ReceivedInvoiceService"/>
/// facade, which delegates to the same <c>ReceivedDocumentWriter</c>. Fixtures are written through a SEPARATE
/// DbContext from the service under test (EF fix-up would hide a missing <c>.Include()</c>).
/// </summary>
public class UnifiedReceivedDocumentTests : IDisposable
{
    private const long OurCompanyId = 1;
    private const long SupplierId = 3;

    private readonly TenantDbContext _seed;
    private readonly TenantDbContext _db;
    private readonly TenantDbContext _read;
    private readonly INumberSequenceService _sequences = Substitute.For<INumberSequenceService>();
    private readonly ITenantReadinessService _readiness = Substitute.For<ITenantReadinessService>();
    private readonly IWebhookPublisher _webhooks = Substitute.For<IWebhookPublisher>();
    private readonly InvoiceService _invoices;
    private readonly ReceivedInvoiceService _facade;

    public UnifiedReceivedDocumentTests()
    {
        var options = new DbContextOptionsBuilder<TenantDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString()).Options;
        _seed = new TenantDbContext(options);
        _db = new TenantDbContext(options);
        _read = new TenantDbContext(options);

        _seed.Currency.Add(new Currency { Id = 1, Code = "CZK", Name = "Koruna", Symbol = "Kc", DecimalPlaces = 2, SortOrder = 1, IsActive = true });
        _seed.Currency.Add(new Currency { Id = 2, Code = "EUR", Name = "Euro", Symbol = "EUR", DecimalPlaces = 2, SortOrder = 2, IsActive = true });
        // Two issuer companies: the received document must pick the LOWEST id, whatever the caller sends.
        _seed.Client.Add(new Client { Id = 5, CompanyName = "Other issuer", RegistrationNumber = "55555555", IsIssuer = true, IsActive = true });
        _seed.Client.Add(new Client { Id = OurCompanyId, CompanyName = "Us", RegistrationNumber = "11111111", IsIssuer = true, IsActive = true });
        _seed.Client.Add(new Client { Id = SupplierId, CompanyName = "Supplier", RegistrationNumber = "33333333", IsActive = true });
        _seed.Client.Add(new Client { Id = 4, CompanyName = "Dormant supplier", RegistrationNumber = "44444444", IsActive = false });
        _seed.SaveChanges();

        _invoices = new InvoiceService(_db, _sequences, _readiness, NullLogger<InvoiceService>.Instance);
        _facade = new ReceivedInvoiceService(_db, NullLogger<ReceivedInvoiceService>.Instance);
    }

    public void Dispose()
    {
        _db.Database.EnsureDeleted();
        _db.Dispose();
        _seed.Dispose();
        _read.Dispose();
    }

    private static CreateInvoiceDto NewReceived(string? number = "SUP-1") => new()
    {
        DocumentType = EDocumentType.ReceivedInvoice, ClientId = SupplierId, IssuerId = 5 /* ignored */, CurrencyId = 1,
        CustomDocumentNumber = number,
        IssueDate = new DateTime(2026, 5, 2, 0, 0, 0, DateTimeKind.Utc),
        DueDate = new DateTime(2026, 5, 20, 0, 0, 0, DateTimeKind.Utc),
        ReceivedDate = new DateTime(2026, 5, 4, 0, 0, 0, DateTimeKind.Utc),
        VariableSymbol = "123456",
        InvoiceItem = [new CreateInvoiceItemDto { Description = "Hosting", Quantity = 2, Unit = "ks", UnitPrice = 100, VatRatePercentage = 21 }]
    };

    private async Task<long> SeedReceivedAsync(EInvoiceStatus status)
    {
        var doc = new Invoice
        {
            DocumentType = EDocumentType.ReceivedInvoice, Status = status, DocumentNumber = "SEED-1", ClientId = SupplierId,
            IssuerId = OurCompanyId, CurrencyId = 1, ReceivedDate = DateTime.UtcNow.Date, IssueDate = DateTime.UtcNow.Date,
            TotalBeforeVat = 100m, TotalVat = 21m, TotalWithVat = 121m,
            InvoiceItem = [new InvoiceItem { Description = "Old", Quantity = 1, UnitPrice = 100, VatRatePercentage = 21, TotalBeforeVat = 100, VatAmount = 21, TotalWithVat = 121 }]
        };
        _seed.Invoice.Add(doc);
        await _seed.SaveChangesAsync();
        return doc.Id;
    }

    // ─── Create: the received type policy ────────────────────────────────────

    [Fact]
    public async Task Create_Received_AppliesTheReceivedTypePolicy()
    {
        var invoices = new InvoiceService(_db, null!, null!, _sequences, _readiness, NullLogger<InvoiceService>.Instance, _webhooks);

        var dto = await invoices.CreateInvoiceAsync(NewReceived());

        dto.DocumentType.ShouldBe(EDocumentType.ReceivedInvoice);
        dto.Status.ShouldBe(EInvoiceStatus.Draft);
        dto.ClientId.ShouldBe(SupplierId);          // the supplier is the client
        dto.ClientName.ShouldBe("Supplier");
        dto.IssuerId.ShouldBe(OurCompanyId);        // set by the server (lowest IsIssuer id), the caller's 5 is ignored
        dto.DocumentNumber.ShouldBe("SUP-1");       // the supplier's number, as given
        dto.ReceivedDate.ShouldBe(new DateTime(2026, 5, 4, 0, 0, 0, DateTimeKind.Utc));
        dto.IssueDate.ShouldBe(new DateTime(2026, 5, 2, 0, 0, 0, DateTimeKind.Utc));
        dto.TaxableSupplyDate.ShouldBe(dto.IssueDate);
        dto.TotalBeforeVat.ShouldBe(200m);
        dto.TotalVat.ShouldBe(42m);
        dto.TotalWithVat.ShouldBe(242m);
        dto.InvoiceItem.Count.ShouldBe(1);

        // Never a number sequence (not drawn, not released), never the issued readiness gate.
        _sequences.ReceivedCalls().ShouldBeEmpty();
        _readiness.ReceivedCalls().ShouldBeEmpty();
        // The one webhook a received document publishes; no invoice.* event.
        await _webhooks.Received(1).PublishReceivedInvoiceEventAsync("received_invoice.created", dto.Id, Arg.Any<CancellationToken>());
        await _webhooks.DidNotReceive().PublishInvoiceEventAsync(Arg.Any<string>(), Arg.Any<long>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Create_Received_WithoutNumber_KeepsItEmpty_AndDefaultsReceivedDateToNow()
    {
        var create = NewReceived(number: null);
        create.ReceivedDate = null;

        var dto = await _invoices.CreateInvoiceAsync(create);

        dto.DocumentNumber.ShouldBeNull(); // never generated, "DRAFT" is an issued placeholder
        dto.ReceivedDate!.Value.ShouldBe(DateTime.UtcNow, TimeSpan.FromMinutes(1));
    }

    [Fact]
    public async Task Create_Received_RefusesWhatDoesNotApply()
    {
        var unknown = NewReceived(); unknown.ClientId = 999;
        var inactive = NewReceived(); inactive.ClientId = 4;
        var textRow = NewReceived(); textRow.InvoiceItem[0].IsTextRow = true;
        var sequence = NewReceived(); sequence.NumberSequenceId = 7;
        var original = NewReceived(); original.OriginalInvoiceId = 7;
        var reverseNoCode = NewReceived(); reverseNoCode.InvoiceItem[0].VatRegime = EVatRegime.ReverseCharge;

        foreach (var bad in new[] { unknown, inactive, textRow, sequence, original, reverseNoCode })
            await Should.ThrowAsync<InvalidOperationException>(() => _invoices.CreateInvoiceAsync(bad));

        (await _read.Invoice.CountAsync()).ShouldBe(0);
    }

    [Fact]
    public async Task Create_Issued_WithReceivedDate_IsRefused()
    {
        var dto = NewReceived();
        dto.DocumentType = EDocumentType.Invoice;
        dto.IssuerId = OurCompanyId;

        await Should.ThrowAsync<InvalidOperationException>(() => _invoices.CreateInvoiceAsync(dto));
    }

    // ─── Update: editing rules ───────────────────────────────────────────────

    [Fact]
    public async Task Update_Received_Draft_EditsSupplierNumberReceivedDateExchangeRateAndItems()
    {
        var id = await SeedReceivedAsync(EInvoiceStatus.Draft);

        var dto = await _invoices.UpdateInvoiceAsync(id, new UpdateInvoiceDto
        {
            DocumentNumber = "SUP-9", ReceivedDate = new DateTime(2026, 6, 1, 0, 0, 0, DateTimeKind.Utc), CurrencyId = 2, ExchangeRate = 25m,
            InvoiceItem = [new CreateInvoiceItemDto { Description = "New", Quantity = 3, Unit = "ks", UnitPrice = 10, VatRatePercentage = 21 }]
        });

        dto!.DocumentNumber.ShouldBe("SUP-9");
        dto.ReceivedDate.ShouldBe(new DateTime(2026, 6, 1, 0, 0, 0, DateTimeKind.Utc));
        dto.ExchangeRate.ShouldBe(25m);
        dto.TotalBeforeVat.ShouldBe(30m);
        dto.TotalWithVat.ShouldBe(36.30m);
        dto.InvoiceItem.Select(i => i.Description).ShouldBe(["New"]); // the old item is replaced, not appended
        (await _read.InvoiceItem.CountAsync(i => i.InvoiceId == id)).ShouldBe(1);
    }

    [Fact]
    public async Task Update_Received_Approved_IsEditable_ButTheExchangeRateIsFrozen()
    {
        var id = await SeedReceivedAsync(EInvoiceStatus.Completed);

        (await _invoices.UpdateInvoiceAsync(id, new UpdateInvoiceDto { Notes = "late note", DocumentNumber = "SUP-10" }))!
            .Notes.ShouldBe("late note");
        var ex = await Should.ThrowAsync<InvalidOperationException>(
            () => _invoices.UpdateInvoiceAsync(id, new UpdateInvoiceDto { ExchangeRate = 24m }));
        ex.Message.ShouldContain("exchange rate");

        (await _read.Invoice.SingleAsync(i => i.Id == id)).ExchangeRate.ShouldBeNull();
    }

    [Theory]
    [InlineData(EInvoiceStatus.Paid)]
    [InlineData(EInvoiceStatus.Deleted)]
    public async Task Update_Received_PaidOrDeleted_IsRefused(EInvoiceStatus status)
    {
        var id = await SeedReceivedAsync(status);

        await Should.ThrowAsync<InvalidOperationException>(
            () => _invoices.UpdateInvoiceAsync(id, new UpdateInvoiceDto { Notes = "x" }));

        (await _read.Invoice.SingleAsync(i => i.Id == id)).Notes.ShouldBeNull();
    }

    [Fact]
    public async Task Update_Received_HonoursExpectedStatus()
    {
        var id = await SeedReceivedAsync(EInvoiceStatus.Draft);

        await Should.ThrowAsync<Fakvio.Application.Exceptions.InvoiceStatusConflictException>(
            () => _invoices.UpdateInvoiceAsync(id, new UpdateInvoiceDto { ExpectedStatus = EInvoiceStatus.Completed }));
    }

    [Fact]
    public async Task Update_Received_RefusesIssuedOnlyFields()
    {
        var id = await SeedReceivedAsync(EInvoiceStatus.Draft);

        await Should.ThrowAsync<InvalidOperationException>(
            () => _invoices.UpdateInvoiceAsync(id, new UpdateInvoiceDto { BankAccountId = 1 }));
    }

    // ─── Copy ────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Copy_Received_CreatesANewReceivedDraft_WithoutANumberSequence()
    {
        var id = await SeedReceivedAsync(EInvoiceStatus.Paid);

        var copy = await _invoices.CopyInvoiceAsync(id);

        copy.Id.ShouldNotBe(id);
        copy.DocumentType.ShouldBe(EDocumentType.ReceivedInvoice);
        copy.Status.ShouldBe(EInvoiceStatus.Draft);
        copy.DocumentNumber.ShouldBeNull(); // the new bill's supplier number is not known yet
        copy.ClientId.ShouldBe(SupplierId);
        copy.IssuerId.ShouldBe(OurCompanyId);
        copy.InvoiceItem.Select(i => i.Description).ShouldBe(["Old"]);
        copy.PaidAmount.ShouldBe(0m);
        _sequences.ReceivedCalls().ShouldBeEmpty();
    }

    // ─── Equivalence of the two entry points ─────────────────────────────────

    [Fact]
    public async Task LegacyFacadeAndUnifiedApi_CreateIdenticalRows_AndReadEachOthersDocuments()
    {
        var unified = await _invoices.CreateInvoiceAsync(NewReceived("SUP-X"));
        var legacy = await _facade.CreateAsync(new CreateReceivedInvoiceDto
        {
            DocumentNumber = "SUP-X", SupplierId = SupplierId, CurrencyId = 1,
            IssueDate = new DateTime(2026, 5, 2, 0, 0, 0, DateTimeKind.Utc),
            DueDate = new DateTime(2026, 5, 20, 0, 0, 0, DateTimeKind.Utc),
            ReceivedDate = new DateTime(2026, 5, 4, 0, 0, 0, DateTimeKind.Utc),
            VariableSymbol = "123456",
            Items = [new CreateReceivedInvoiceItemDto { Description = "Hosting", Quantity = 2, Unit = "ks", UnitPrice = 100, VatRatePercentage = 21 }]
        });

        ScalarColumns(unified.Id).ShouldContain("DocumentType=ReceivedInvoice"); // the comparison is not vacuous
        ScalarColumns(unified.Id).ShouldBe(ScalarColumns(legacy.Id));
        ItemColumns(unified.Id).ShouldNotBeEmpty();
        ItemColumns(unified.Id).ShouldBe(ItemColumns(legacy.Id));

        // Each API reads the document the other one created.
        (await _facade.GetByIdAsync(unified.Id))!.SupplierId.ShouldBe(SupplierId);
        (await _invoices.GetInvoiceByIdAsync(legacy.Id))!.DocumentType.ShouldBe(EDocumentType.ReceivedInvoice);

        // ...and the same update has the same effect through both.
        await _invoices.UpdateInvoiceAsync(unified.Id, new UpdateInvoiceDto { Notes = "n", DocumentNumber = "SUP-Y" });
        await _facade.UpdateAsync(legacy.Id, new UpdateReceivedInvoiceDto { Notes = "n", DocumentNumber = "SUP-Y" });
        ScalarColumns(unified.Id).ShouldBe(ScalarColumns(legacy.Id));
    }

    /// <summary>All scalar columns of a document except identity and audit stamps, as "Name=Value" strings.</summary>
    private List<string> ScalarColumns(long id)
    {
        return ColumnsOf(_read, _read.Invoice.AsNoTracking().Single(i => i.Id == id));
    }

    private List<string> ItemColumns(long invoiceId)
    {
        var items = _read.InvoiceItem.AsNoTracking().Where(i => i.InvoiceId == invoiceId).OrderBy(i => i.OrderIndex).ToList();
        return items.SelectMany(i => ColumnsOf(_read, i)).ToList();
    }

    private static List<string> ColumnsOf(TenantDbContext ctx, object entity)
    {
        var skip = new HashSet<string> { "Id", "InvoiceId", "CreatedAt", "UpdatedAt" };
        ctx.ChangeTracker.Clear(); // the same row may have been attached by an earlier call
        return ctx.Attach(entity).Properties
            .Where(p => !skip.Contains(p.Metadata.Name))
            .OrderBy(p => p.Metadata.Name, StringComparer.Ordinal)
            .Select(p => $"{p.Metadata.Name}={p.CurrentValue}")
            .ToList();
    }
}

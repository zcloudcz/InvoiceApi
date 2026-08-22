using Fakvio.Application.Service;
using Fakvio.Contracts.Dto.Invoice;
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
/// Issue #181 — a failed <c>InvoiceService.CreateInvoiceAsync</c> must leave the database
/// exactly as it found it.
///
/// The defect: the invoice used to be saved BEFORE the document number was generated, so any
/// failure after that save left a half-created row with <c>DocumentNumber = "DRAFT"</c>.
/// The unique index does not see such a row (it is filtered by <c>&lt;&gt; 'DRAFT'</c>), but the
/// row still carries the VariableSymbol the caller supplied. With a manually entered VS the
/// phantom therefore blocked the retry that the error message from issue #155 explicitly asks
/// the user to perform — and it blamed a duplicate VS, which points at the wrong cause.
///
/// Every test here asserts through a FRESH DbContext, so it reads what is really stored
/// instead of what the change tracker still remembers.
///
/// Seeding also goes through a separate, disposed context (DEVGUIDE §12): a test that seeds
/// through the same context the service later uses gets EF relationship fixup for free, which
/// hides missing Includes and makes the identity map look nothing like a real HTTP request.
/// </summary>
public class InvoiceServiceCreateRollbackTests : IDisposable
{
    // IDs of the seeded reference data — kept as constants so seed and assertions stay in sync.
    private const long CustomerId = 1;
    private const long IssuerId = 2;
    private const long CurrencyId = 1;

    /// <summary>The document number the (healthy) number sequence hands out in these tests.</summary>
    private const string GeneratedNumber = "INV2026001";

    /// <summary>VariableSymbol the user types by hand — deliberately unrelated to any number.</summary>
    private const string ManualVariableSymbol = "5550001";

    /// <summary>
    /// One in-memory store shared by every context this class opens. Each context is a stand-in
    /// for one HTTP request; they must all see the same data, exactly like real requests do.
    /// </summary>
    private readonly string _databaseName = Guid.NewGuid().ToString();

    private readonly INumberSequenceService _numberSequence = Substitute.For<INumberSequenceService>();
    private readonly List<TenantDbContext> _openContexts = new();

    public InvoiceServiceCreateRollbackTests()
    {
        // Default: a healthy sequence. Tests that need a broken one override this.
        _numberSequence
            .GenerateNextNumberForDocumentTypeAsync(
                Arg.Any<EDocumentType>(), Arg.Any<DateTime>(),
                Arg.Any<string?>(), Arg.Any<string?>(),
                Arg.Any<CancellationToken>())
            .Returns(GeneratedNumber);

        SeedReferenceData();
    }

    public void Dispose()
    {
        foreach (var context in _openContexts)
            context.Dispose();

        using var cleanup = NewContext();
        cleanup.Database.EnsureDeleted();
    }

    // ── Fixture helpers ───────────────────────────────────────────────────────

    /// <summary>Opens another context over the shared in-memory store.</summary>
    private TenantDbContext NewContext()
    {
        var options = new DbContextOptionsBuilder<TenantDbContext>()
            .UseInMemoryDatabase(databaseName: _databaseName)
            .Options;

        return new TenantDbContext(options);
    }

    /// <summary>
    /// Builds the service on a brand-new context — the unit-test equivalent of one HTTP request.
    /// The context is kept alive (and disposed with the fixture) so assertions can still be made
    /// against a different, independent context.
    /// </summary>
    private InvoiceService NewService()
    {
        var context = NewContext();
        _openContexts.Add(context);

        return new InvoiceService(context, _numberSequence, Substitute.For<ILogger<InvoiceService>>());
    }

    /// <summary>
    /// Seeds customer, issuer and currency through a context that is disposed immediately.
    /// Entities are saved one by one — the in-memory provider generates deterministic Ids that way.
    /// </summary>
    private void SeedReferenceData()
    {
        using var seed = NewContext();

        seed.Client.Add(new Client
        {
            Id = CustomerId,
            CompanyName = "Customer A",
            RegistrationNumber = "REG001",
            IsIssuer = false,
            IsActive = true
        });
        seed.SaveChanges();

        seed.Client.Add(new Client
        {
            Id = IssuerId,
            CompanyName = "My Company",
            RegistrationNumber = "REG002",
            IsIssuer = true,
            IsActive = true,
            IsVatPayer = false
        });
        seed.SaveChanges();

        seed.Currency.Add(new Currency
        {
            Id = CurrencyId,
            Code = "CZK",
            Name = "Czech Koruna",
            Symbol = "Kc",
            DecimalPlaces = 2,
            SortOrder = 1,
            IsActive = true
        });
        seed.SaveChanges();
    }

    /// <summary>Seeds an invoice that already occupies a VariableSymbol.</summary>
    private void SeedExistingInvoice(string variableSymbol, string documentNumber)
    {
        using var seed = NewContext();

        seed.Invoice.Add(new Invoice
        {
            DocumentNumber = documentNumber,
            DocumentType = EDocumentType.Invoice,
            Status = EInvoiceStatus.Completed,
            ClientId = CustomerId,
            IssuerId = IssuerId,
            CurrencyId = CurrencyId,
            VariableSymbol = variableSymbol,
            IssueDate = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc),
            TaxableSupplyDate = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc),
            TotalBeforeVat = 1000m,
            TotalVat = 0m,
            TotalWithVat = 1000m
        });
        seed.SaveChanges();
    }

    /// <summary>
    /// A minimal, valid invoice request. <paramref name="manualVariableSymbol"/> mimics an API or
    /// import caller that supplies its own VS (the UI always sends null — see InvoiceDetail.razor).
    /// </summary>
    private static CreateInvoiceDto MakeInvoiceDto(string? manualVariableSymbol = null)
    {
        return new CreateInvoiceDto
        {
            DocumentType = EDocumentType.Invoice,
            ClientId = CustomerId,
            IssuerId = IssuerId,
            CurrencyId = CurrencyId,
            VariableSymbol = manualVariableSymbol,
            VariableSymbolIsManualOverride = manualVariableSymbol is not null,
            InvoiceItem = new List<CreateInvoiceItemDto>
            {
                new()
                {
                    OrderIndex = 1,
                    Description = "Service",
                    Quantity = 1,
                    Unit = "pcs",
                    UnitPrice = 1000
                }
            }
        };
    }

    /// <summary>Makes the default number sequence fail the way a missing series does.</summary>
    private void BreakNumberSequence(Func<bool> isBroken)
    {
        _numberSequence
            .GenerateNextNumberForDocumentTypeAsync(
                Arg.Any<EDocumentType>(), Arg.Any<DateTime>(),
                Arg.Any<string?>(), Arg.Any<string?>(),
                Arg.Any<CancellationToken>())
            .Returns(_ => isBroken()
                ? throw new InvalidOperationException("No default number sequence configured for Invoice")
                : GeneratedNumber);
    }

    // ── Tests ─────────────────────────────────────────────────────────────────

    /// <summary>
    /// AC 1: a failed document-number generation must not leave a DRAFT row behind.
    /// Before the fix this test found one invoice with DocumentNumber = "DRAFT".
    /// </summary>
    [Fact]
    public async Task CreateInvoice_WhenNumberGenerationFails_PersistsNothing()
    {
        // Arrange — the series is missing, so numbering throws
        BreakNumberSequence(() => true);

        // Act
        await Should.ThrowAsync<InvalidOperationException>(
            () => NewService().CreateInvoiceAsync(MakeInvoiceDto(ManualVariableSymbol)));

        // Assert — read through a fresh context: the database must be untouched
        await using var verify = NewContext();
        (await verify.Invoice.ToListAsync()).ShouldBeEmpty();
        (await verify.InvoiceItem.ToListAsync()).ShouldBeEmpty();
    }

    /// <summary>
    /// AC 2 — the actual defect of issue #181.
    ///
    /// The user supplies a VS by hand, numbering fails, and the error message tells him to
    /// repeat the action. The retry is a second HTTP request, so it runs on its own context.
    /// Before the fix the orphaned draft from attempt 1 still held the VS and the retry was
    /// rejected with "An invoice with Variable Symbol '5550001' already exists." — advice the
    /// user could not follow, blaming a cause that did not exist.
    /// </summary>
    [Fact]
    public async Task CreateInvoice_AfterFailedNumberGeneration_AcceptsTheSameManualVsOnRetry()
    {
        var sequenceIsBroken = true;
        BreakNumberSequence(() => sequenceIsBroken);

        // Act 1 — first attempt fails on numbering, exactly as issue #155 intends
        var failure = await Should.ThrowAsync<InvalidOperationException>(
            () => NewService().CreateInvoiceAsync(MakeInvoiceDto(ManualVariableSymbol)));
        failure.Message.ShouldContain("number sequence");

        // Arrange — the admin activates the series, the user repeats the action
        sequenceIsBroken = false;

        // Act 2 — the retry must go through with the very same manual VS
        var created = await NewService().CreateInvoiceAsync(MakeInvoiceDto(ManualVariableSymbol));

        // Assert — the retry produced the one and only invoice, keeping the user's VS
        created.VariableSymbol.ShouldBe(ManualVariableSymbol);
        created.DocumentNumber.ShouldBe(GeneratedNumber);

        await using var verify = NewContext();
        var stored = await verify.Invoice.ToListAsync();
        stored.Count.ShouldBe(1, customMessage: "The failed attempt must not survive as a second row");
        stored[0].VariableSymbol.ShouldBe(ManualVariableSymbol);
    }

    /// <summary>
    /// AC 3, duplicate-VS branch: the check that runs AFTER the number is generated shares the
    /// old pattern, so it left the same orphan behind. The auto-derived VS ("2026001", the digits
    /// of INV2026001) collides with an invoice that already exists.
    /// </summary>
    [Fact]
    public async Task CreateInvoice_WhenDerivedVariableSymbolCollides_PersistsNothing()
    {
        // Arrange — an existing invoice already owns the VS that will be derived
        SeedExistingInvoice(variableSymbol: "2026001", documentNumber: "INV2026000");

        // Act — VS is derived from the generated number, so the collision is only found
        // after a number has been drawn
        var ex = await Should.ThrowAsync<InvalidOperationException>(
            () => NewService().CreateInvoiceAsync(MakeInvoiceDto()));
        ex.Message.ShouldContain("Variable Symbol");

        // Assert — only the seeded invoice is left; no DRAFT row was created
        await using var verify = NewContext();
        var stored = await verify.Invoice.ToListAsync();
        stored.Count.ShouldBe(1, customMessage: "Only the pre-existing invoice may remain");
        stored[0].DocumentNumber.ShouldBe("INV2026000");
        (await verify.InvoiceItem.ToListAsync()).ShouldBeEmpty();
    }

    /// <summary>
    /// AC 3, manual-VS branch: the early duplicate check rejects the request before a number is
    /// drawn. Nothing is stored, and — just as importantly — the number sequence is never asked
    /// for a number, so a rejected request does not burn one and leave a gap in the series.
    /// </summary>
    [Fact]
    public async Task CreateInvoice_WhenManualVariableSymbolIsTaken_PersistsNothingAndDrawsNoNumber()
    {
        // Arrange
        SeedExistingInvoice(variableSymbol: ManualVariableSymbol, documentNumber: "INV2026000");

        // Act
        var ex = await Should.ThrowAsync<InvalidOperationException>(
            () => NewService().CreateInvoiceAsync(MakeInvoiceDto(ManualVariableSymbol)));
        ex.Message.ShouldContain("Variable Symbol");

        // Assert — database untouched
        await using var verify = NewContext();
        (await verify.Invoice.ToListAsync()).Count.ShouldBe(1);
        (await verify.InvoiceItem.ToListAsync()).ShouldBeEmpty();

        // Assert — no document number was consumed
        await _numberSequence.DidNotReceive().GenerateNextNumberForDocumentTypeAsync(
            Arg.Any<EDocumentType>(), Arg.Any<DateTime>(),
            Arg.Any<string?>(), Arg.Any<string?>(),
            Arg.Any<CancellationToken>());
    }

    /// <summary>
    /// AC 3, happy path: moving the write to the end of the method must not change what a
    /// successful creation produces — one invoice, its items, the generated number, and the VS
    /// derived from that number.
    /// </summary>
    [Fact]
    public async Task CreateInvoice_OnSuccess_StoresTheInvoiceWithItsItems()
    {
        // Act
        var created = await NewService().CreateInvoiceAsync(MakeInvoiceDto());

        // Assert — the returned DTO
        created.Id.ShouldBeGreaterThan(0);
        created.DocumentNumber.ShouldBe(GeneratedNumber);
        created.VariableSymbol.ShouldBe("2026001");

        // Assert — and the same thing is really in the database, items included
        await using var verify = NewContext();
        var stored = await verify.Invoice.Include(i => i.InvoiceItem).SingleAsync();
        stored.DocumentNumber.ShouldBe(GeneratedNumber);
        stored.VariableSymbol.ShouldBe("2026001");
        stored.Status.ShouldBe(EInvoiceStatus.Draft);
        stored.InvoiceItem.Count.ShouldBe(1);
        stored.InvoiceItem.Single().Description.ShouldBe("Service");
    }
}

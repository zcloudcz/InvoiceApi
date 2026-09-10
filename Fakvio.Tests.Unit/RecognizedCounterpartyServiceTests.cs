using Fakvio.Application.Service;
using Fakvio.Contracts.Dto.RecognizedCounterparty;
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
/// CRUD behaviour of <see cref="RecognizedCounterpartyService"/> — including
/// the automatic rescan on save and the transaction reset on delete/deactivate.
/// The matcher itself is covered in RecognizedCounterpartyMatchingTests.
/// </summary>
public class RecognizedCounterpartyServiceTests : IDisposable
{
    private readonly TenantDbContext _context;
    private readonly IPaymentMatchingService _matcher = Substitute.For<IPaymentMatchingService>();
    private readonly RecognizedCounterpartyService _sut;

    public RecognizedCounterpartyServiceTests()
    {
        var options = new DbContextOptionsBuilder<TenantDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;
        _context = new TenantDbContext(options);
        _matcher.RescanUnmatchedAsync(Arg.Any<CancellationToken>()).Returns(3);
        _sut = new RecognizedCounterpartyService(
            _context, _matcher, Substitute.For<ILogger<RecognizedCounterpartyService>>());
    }

    public void Dispose()
    {
        _context.Database.EnsureDeleted();
        _context.Dispose();
        GC.SuppressFinalize(this);
    }

    private static SaveRecognizedCounterpartyRequest MakeRequest(
        string label = "OSSZ", string account = "1011-7724311/0710", bool isActive = true) => new()
    {
        Label = label,
        CounterpartyAccount = account,
        Category = EPaymentCategory.SocialInsurance,
        IsActive = isActive,
    };

    // ─── Create ───────────────────────────────────────────────────────────

    [Fact]
    public async Task Create_PersistsEntry_AndTriggersRescan()
    {
        var response = await _sut.CreateAsync(MakeRequest());

        response.Entry.Id.ShouldBeGreaterThan(0);
        response.Entry.Label.ShouldBe("OSSZ");
        response.Entry.Category.ShouldBe(EPaymentCategory.SocialInsurance);
        // Rescan ran automatically and its result is propagated for the UI snackbar.
        response.RecognizedCount.ShouldBe(3);
        await _matcher.Received(1).RescanUnmatchedAsync(Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Create_InactiveEntry_SkipsRescan()
    {
        var response = await _sut.CreateAsync(MakeRequest(isActive: false));

        response.RecognizedCount.ShouldBe(0);
        await _matcher.DidNotReceive().RescanUnmatchedAsync(Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Create_MissingLabel_Throws()
    {
        await Should.ThrowAsync<ArgumentException>(
            () => _sut.CreateAsync(new SaveRecognizedCounterpartyRequest
            {
                Label = "",
                CounterpartyAccount = "123/0100",
            }));
    }

    [Fact]
    public async Task Create_NeitherAccountNorNamePattern_Throws()
    {
        await Should.ThrowAsync<ArgumentException>(
            () => _sut.CreateAsync(new SaveRecognizedCounterpartyRequest
            {
                Label = "Label",
                CounterpartyAccount = null,
                CounterpartyNamePattern = null,
            }));
    }

    [Fact]
    public async Task Create_NamePatternOnly_IsValid()
    {
        // Card-payment entries have no account — a name pattern alone suffices.
        var response = await _sut.CreateAsync(new SaveRecognizedCounterpartyRequest
        {
            Label = "Anthropic — Claude",
            CounterpartyNamePattern = "ANTHROPIC",
            Category = EPaymentCategory.Other,
        });

        response.Entry.CounterpartyAccount.ShouldBeNull();
        response.Entry.CounterpartyNamePattern.ShouldBe("ANTHROPIC");
    }

    // ─── Read ─────────────────────────────────────────────────────────────

    [Fact]
    public async Task GetAll_ReturnsAllOrderedByLabel_IncludingInactive()
    {
        await _sut.CreateAsync(MakeRequest(label: "Zdravotní", isActive: false));
        await _sut.CreateAsync(MakeRequest(label: "Alfa"));

        var all = await _sut.GetAllAsync();

        all.Count.ShouldBe(2);
        all[0].Label.ShouldBe("Alfa");
        all[1].Label.ShouldBe("Zdravotní");
        all[1].IsActive.ShouldBeFalse();
    }

    [Fact]
    public async Task GetById_UnknownId_ReturnsNull()
    {
        (await _sut.GetByIdAsync(999)).ShouldBeNull();
    }

    // ─── Update ───────────────────────────────────────────────────────────

    [Fact]
    public async Task Update_ChangesFields_AndTriggersRescan()
    {
        var created = await _sut.CreateAsync(MakeRequest());
        _matcher.ClearReceivedCalls();

        var response = await _sut.UpdateAsync(created.Entry.Id, new SaveRecognizedCounterpartyRequest
        {
            Label = "OSSZ Praha",
            CounterpartyAccount = "999/0710",
            VariableSymbol = "123",
            IsActive = true,
        });

        response.ShouldNotBeNull();
        response.Entry.Label.ShouldBe("OSSZ Praha");
        response.Entry.VariableSymbol.ShouldBe("123");
        response.RecognizedCount.ShouldBe(3);
        await _matcher.Received(1).RescanUnmatchedAsync(Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Update_UnknownId_ReturnsNull()
    {
        (await _sut.UpdateAsync(999, MakeRequest())).ShouldBeNull();
    }

    [Fact]
    public async Task Update_Deactivation_ResetsRecognizedTransactions()
    {
        var created = await _sut.CreateAsync(MakeRequest());
        var tx = AddRecognizedTransaction(created.Entry.Id);

        await _sut.UpdateAsync(created.Entry.Id, MakeRequest(isActive: false));

        var reloaded = await _context.BankTransaction.AsNoTracking().FirstAsync(t => t.Id == tx.Id);
        reloaded.RecognizedCounterpartyId.ShouldBeNull();
        reloaded.MatchStatus.ShouldBe(EMatchStatus.Unmatched);
    }

    // ─── Delete ───────────────────────────────────────────────────────────

    [Fact]
    public async Task Delete_RemovesEntry_AndResetsRecognizedTransactions()
    {
        var created = await _sut.CreateAsync(MakeRequest());
        var tx = AddRecognizedTransaction(created.Entry.Id);

        var deleted = await _sut.DeleteAsync(created.Entry.Id);

        deleted.ShouldBeTrue();
        (await _context.RecognizedCounterparty.CountAsync()).ShouldBe(0);

        var reloaded = await _context.BankTransaction.AsNoTracking().FirstAsync(t => t.Id == tx.Id);
        reloaded.RecognizedCounterpartyId.ShouldBeNull();
        reloaded.MatchStatus.ShouldBe(EMatchStatus.Unmatched);
    }

    [Fact]
    public async Task Delete_UnknownId_ReturnsFalse()
    {
        (await _sut.DeleteAsync(999)).ShouldBeFalse();
    }

    // ─── Functions wrapper smoke tests (API + Functions parity) ───────────

    // ─── Helpers ──────────────────────────────────────────────────────────

    /// <summary>Seeds a bank account + a transaction already recognized by the given entry.</summary>
    private BankTransaction AddRecognizedTransaction(long entryId)
    {
        var client = new Client { RegistrationNumber = "11111111", CompanyName = "C", IsIssuer = true, IsActive = true };
        _context.Client.Add(client);
        _context.SaveChanges();

        var account = new BankAccount { ClientId = client.Id, AccountNumber = "1/0100" };
        _context.BankAccount.Add(account);
        _context.SaveChanges();

        var tx = new BankTransaction
        {
            BankAccountId = account.Id,
            DeduplicationHash = Guid.NewGuid().ToString("N"),
            TransactionDate = DateTime.UtcNow,
            Amount = 100m,
            CurrencyCode = "CZK",
            Direction = EPaymentDirection.Outgoing,
            ImportSource = EImportSource.Manual,
            MatchStatus = EMatchStatus.Recognized,
            RecognizedCounterpartyId = entryId,
        };
        _context.BankTransaction.Add(tx);
        _context.SaveChanges();
        return tx;
    }
}

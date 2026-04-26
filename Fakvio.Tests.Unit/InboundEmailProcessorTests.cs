using Fakvio.Application.Service;
using Fakvio.Domain.Entities;
using Fakvio.Domain.Enums;
using Fakvio.Infrastructure.Data;
using Fakvio.Infrastructure.Service;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using NSubstitute;
using NSubstitute.ExceptionExtensions;
using Shouldly;

namespace Fakvio.Tests.Unit;

/// <summary>
/// Behaviour tests for <see cref="InboundEmailProcessor"/> — the orchestrator
/// that ties together the mailbox, archive, AI parser, and matcher.
///
/// The parser is substituted with NSubstitute so we can script AI outcomes
/// (success / null / throw) without calling out to a real model.
/// </summary>
public class InboundEmailProcessorTests : IDisposable
{
    private readonly TenantDbContext _context;
    private readonly IBankEmailParser _parser;
    private readonly IPaymentMatchingService _matcher;
    private readonly InboundEmailProcessor _sut;

    private long _bankAccountId;
    private long _mailboxId;
    private DateTime _activeFrom;

    public InboundEmailProcessorTests()
    {
        var options = new DbContextOptionsBuilder<TenantDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;
        _context = new TenantDbContext(options);

        _parser = Substitute.For<IBankEmailParser>();
        _matcher = Substitute.For<IPaymentMatchingService>();
        _sut = new InboundEmailProcessor(
            _context,
            _parser,
            _matcher,
            Substitute.For<ILogger<InboundEmailProcessor>>());

        SeedMailbox();
    }

    public void Dispose()
    {
        _context.Database.EnsureDeleted();
        _context.Dispose();
        GC.SuppressFinalize(this);
    }

    [Fact]
    public async Task DeactivatedMailbox_SkipsIngest()
    {
        var mailbox = _context.BankAccountMailbox.First();
        mailbox.IsActive = false;
        await _context.SaveChangesAsync();

        var result = await _sut.ProcessAsync(BuildPayload(), companyId: 1);

        result.ShouldBe(EParseStatus.Ignored);
        (await _context.InboundEmail.AnyAsync()).ShouldBeFalse();
        await _parser.DidNotReceiveWithAnyArgs().ParseAsync(default!, default);
    }

    [Fact]
    public async Task EmailOlderThanActiveFrom_Skipped()
    {
        var payload = BuildPayload(emailDate: _activeFrom.AddDays(-1));

        var result = await _sut.ProcessAsync(payload, companyId: 1);

        result.ShouldBe(EParseStatus.Ignored);
        (await _context.InboundEmail.AnyAsync()).ShouldBeFalse();
    }

    [Fact]
    public async Task DuplicateEmail_NotIngestedTwice()
    {
        _parser.ParseAsync(Arg.Any<BankEmailInput>(), Arg.Any<long?>(), Arg.Any<CancellationToken>())
            .Returns((BankEmailParsed?)null); // ends in NeedsReview

        var payload = BuildPayload();

        var first = await _sut.ProcessAsync(payload, companyId: 1);
        var second = await _sut.ProcessAsync(payload, companyId: 1);

        first.ShouldBe(EParseStatus.NeedsReview);
        second.ShouldBe(EParseStatus.NeedsReview); // same InboundEmail's status returned
        (await _context.InboundEmail.CountAsync()).ShouldBe(1);
    }

    [Fact]
    public async Task ParserReturnsNull_MarksNeedsReview()
    {
        _parser.ParseAsync(Arg.Any<BankEmailInput>(), Arg.Any<long?>(), Arg.Any<CancellationToken>())
            .Returns((BankEmailParsed?)null);

        await _sut.ProcessAsync(BuildPayload(), companyId: 1);

        var archive = await _context.InboundEmail.FirstAsync();
        archive.ParseStatus.ShouldBe(EParseStatus.NeedsReview);
        archive.BankTransactionId.ShouldBeNull();
        archive.ParseAttempts.ShouldBe(1);
    }

    [Fact]
    public async Task ParserThrows_MarksFailed()
    {
        _parser.ParseAsync(Arg.Any<BankEmailInput>(), Arg.Any<long?>(), Arg.Any<CancellationToken>())
            .ThrowsAsyncForAnyArgs(new InvalidOperationException("boom"));

        await _sut.ProcessAsync(BuildPayload(), companyId: 1);

        var archive = await _context.InboundEmail.FirstAsync();
        archive.ParseStatus.ShouldBe(EParseStatus.Failed);
        archive.ParseError.ShouldContain("boom");
    }

    [Fact]
    public async Task ParserSuccess_CreatesTransactionAndInvokesMatcher()
    {
        var parsed = new BankEmailParsed(
            Amount: 1234.56m,
            CurrencyCode: "CZK",
            Direction: EPaymentDirection.Incoming,
            TransactionDate: DateTime.UtcNow,
            VariableSymbol: "2026001",
            ConstantSymbol: null,
            SpecificSymbol: null,
            CounterpartyAccount: "1234/0100",
            CounterpartyName: "Acme",
            Message: null,
            Confidence: 0.9m,
            ModelUsed: "TestModel");

        _parser.ParseAsync(Arg.Any<BankEmailInput>(), Arg.Any<long?>(), Arg.Any<CancellationToken>())
            .Returns(parsed);

        await _sut.ProcessAsync(BuildPayload(), companyId: 1);

        var archive = await _context.InboundEmail.FirstAsync();
        archive.ParseStatus.ShouldBe(EParseStatus.Parsed);
        archive.BankTransactionId.ShouldNotBeNull();

        var tx = await _context.BankTransaction.FirstAsync();
        tx.Amount.ShouldBe(1234.56m);
        tx.VariableSymbol.ShouldBe("2026001");
        tx.ImportSource.ShouldBe(EImportSource.InboundEmail);
        tx.ParserModel.ShouldBe("TestModel");

        await _matcher.Received(1).MatchAsync(tx.Id, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task MailboxStatsUpdated_OnIngest()
    {
        _parser.ParseAsync(Arg.Any<BankEmailInput>(), Arg.Any<long?>(), Arg.Any<CancellationToken>())
            .Returns((BankEmailParsed?)null);

        await _sut.ProcessAsync(BuildPayload(), companyId: 1);

        var reloaded = await _context.BankAccountMailbox.FirstAsync();
        reloaded.EmailsReceivedCount.ShouldBe(1);
        reloaded.LastEmailReceivedAt.ShouldNotBeNull();
    }

    [Fact]
    public void ComputeDeduplicationHash_SameInputYieldsSameHash()
    {
        var p1 = new InboundEmailPayload(1, "msg@x", "42", new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc),
            "from@x", null, "to@x", "subj", null, "body", null);
        var p2 = new InboundEmailPayload(1, "msg@x", "42", new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc),
            "different-from@x", null, "to@x", "subj", null, "body", null);

        var h1 = InboundEmailProcessor.ComputeDeduplicationHash(1, p1);
        var h2 = InboundEmailProcessor.ComputeDeduplicationHash(1, p2);

        // Hash uses MessageId + IMAP UID + timestamp — not From.
        h1.ShouldBe(h2);
    }

    // ─── Helpers ─────────────────────────────────────────────────────────

    private void SeedMailbox()
    {
        var bankAccount = new BankAccount
        {
            ClientId = 1,
            AccountNumber = "1/0100",
            CurrencyCode = "CZK",
        };
        _context.BankAccount.Add(bankAccount);
        _context.SaveChanges();
        _bankAccountId = bankAccount.Id;

        _activeFrom = DateTime.UtcNow.AddDays(-1);
        var mailbox = new BankAccountMailbox
        {
            BankAccountId = _bankAccountId,
            InboundAlias = "pay-testabcdef",
            IsActive = true,
            ActiveFrom = _activeFrom,
        };
        _context.BankAccountMailbox.Add(mailbox);
        _context.SaveChanges();
        _mailboxId = mailbox.Id;
    }

    private InboundEmailPayload BuildPayload(string? messageId = null, DateTime? emailDate = null)
    {
        return new InboundEmailPayload(
            BankAccountMailboxId: _mailboxId,
            MessageId: messageId ?? $"<{Guid.NewGuid()}@bank.cz>",
            ImapUid: "42",
            ServerReceivedAt: DateTime.UtcNow,
            FromAddress: "noreply@kb.cz",
            FromDisplayName: "KB Oznameni",
            ToAddress: "pay-testabcdef@pay.fakvio.cz",
            Subject: "Oznameni o transakci",
            EmailDate: emailDate ?? DateTime.UtcNow,
            TextBody: "Castka: 1234.56 CZK VS: 2026001",
            HtmlBody: null);
    }
}

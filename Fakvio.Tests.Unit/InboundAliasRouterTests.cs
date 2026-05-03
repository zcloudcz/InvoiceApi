using Fakvio.Domain.Entities;
using Fakvio.Infrastructure.Data;
using Fakvio.Infrastructure.Service;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using MimeKit;
using NSubstitute;
using Shouldly;

namespace Fakvio.Tests.Unit;

/// <summary>
/// Behaviour tests for <see cref="InboundAliasRouter"/>.
///
/// Each test builds a <see cref="MimeMessage"/> with the relevant headers set,
/// seeds a <see cref="MasterMailboxIndex"/> row into an in-memory
/// <see cref="MasterDbContext"/>, and then asserts that the router either
/// resolves the correct alias or returns <c>null</c>.
///
/// Test naming follows the convention from the issue #67 acceptance criteria:
///   <c>Resolve_&lt;Scenario&gt;_&lt;ExpectedOutcome&gt;</c>
/// </summary>
public class InboundAliasRouterTests : IDisposable
{
    // ─── Shared infrastructure ────────────────────────────────────────────────

    private readonly MasterDbContext _master;
    private readonly InboundAliasRouter _sut;

    // Alias constants used across tests.
    private const string ActiveAlias = "pay-active123";
    private const string RetiredAlias = "pay-retired456";
    private const string AnotherActiveAlias = "pay-other789";
    private const string InboundDomain = "fakvio.cz";

    public InboundAliasRouterTests()
    {
        var opts = new DbContextOptionsBuilder<MasterDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;
        _master = new MasterDbContext(opts);

        // Seed master index rows used by most tests.
        _master.MasterMailboxIndex.AddRange(
            new MasterMailboxIndex
            {
                InboundAlias = ActiveAlias,
                TenantSchema = "tenant_1",
                TenantBankAccountMailboxId = 10,
                IsAliasRetired = false,
            },
            new MasterMailboxIndex
            {
                InboundAlias = RetiredAlias,
                TenantSchema = "tenant_2",
                TenantBankAccountMailboxId = 20,
                IsAliasRetired = true,
            },
            new MasterMailboxIndex
            {
                InboundAlias = AnotherActiveAlias,
                TenantSchema = "tenant_3",
                TenantBankAccountMailboxId = 30,
                IsAliasRetired = false,
            }
        );
        _master.SaveChanges();

        _sut = new InboundAliasRouter(Substitute.For<ILogger<InboundAliasRouter>>());
    }

    public void Dispose()
    {
        _master.Database.EnsureDeleted();
        _master.Dispose();
        GC.SuppressFinalize(this);
    }

    // ─── Helpers ──────────────────────────────────────────────────────────────

    /// <summary>
    /// Creates a minimal MimeMessage with the given To: address and no other
    /// routing headers so tests can add only what they need.
    /// </summary>
    private static MimeMessage BuildMessage(string? toAddress = null)
    {
        var msg = new MimeMessage();
        if (toAddress is not null)
            msg.To.Add(MailboxAddress.Parse(toAddress));
        return msg;
    }

    /// <summary>Adds a raw header value to the message.</summary>
    private static void AddHeader(MimeMessage msg, string field, string value)
        => msg.Headers.Add(field, value);

    // ─── Tests ────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Resolve_MatchesByTo_WhenToContainsKnownAlias()
    {
        // Arrange: only To: is set, no fallback headers.
        var msg = BuildMessage($"{ActiveAlias}@{InboundDomain}");

        // Act
        var result = await _sut.ResolveAsync(msg, _master, InboundDomain);

        // Assert
        result.ShouldNotBeNull();
        result.MatchedAlias.ShouldBe(ActiveAlias);
        result.MatchedHeader.ShouldBe("To");
        result.MasterIndexEntry.TenantSchema.ShouldBe("tenant_1");
    }

    [Fact]
    public async Task Resolve_MatchesByDeliveredTo_WhenToHeaderMismatches()
    {
        // Arrange: To: points to a catch-all address; real alias is in Delivered-To.
        var msg = BuildMessage($"inbox@{InboundDomain}");
        AddHeader(msg, "Delivered-To", $"{ActiveAlias}@{InboundDomain}");

        // Act
        var result = await _sut.ResolveAsync(msg, _master, InboundDomain);

        // Assert: Delivered-To wins over To: because it appears earlier in the chain.
        result.ShouldNotBeNull();
        result.MatchedAlias.ShouldBe(ActiveAlias);
        result.MatchedHeader.ShouldBe("Delivered-To");
    }

    [Fact]
    public async Task Resolve_MatchesByXOriginalTo_PostfixCatchAll()
    {
        // Simulate a Postfix catch-all setup: To: has the catch-all address,
        // X-Original-To carries the original RCPT TO.
        var msg = BuildMessage($"catchall@{InboundDomain}");
        AddHeader(msg, "X-Original-To", $"{ActiveAlias}@{InboundDomain}");

        // Act
        var result = await _sut.ResolveAsync(msg, _master, InboundDomain);

        // Assert
        result.ShouldNotBeNull();
        result.MatchedAlias.ShouldBe(ActiveAlias);
        result.MatchedHeader.ShouldBe("X-Original-To");
    }

    [Fact]
    public async Task Resolve_MatchesByEnvelopeTo_EximForward()
    {
        // Simulate an Exim forwarding scenario.
        var msg = BuildMessage($"forward@{InboundDomain}");
        AddHeader(msg, "Envelope-To", $"{ActiveAlias}@{InboundDomain}");

        // Act
        var result = await _sut.ResolveAsync(msg, _master, InboundDomain);

        // Assert
        result.ShouldNotBeNull();
        result.MatchedAlias.ShouldBe(ActiveAlias);
        result.MatchedHeader.ShouldBe("Envelope-To");
    }

    [Fact]
    public async Task Resolve_MatchesByReceivedForHeader_AsLastResort()
    {
        // No address-based headers match; the alias is in the Received: "for" clause.
        var msg = BuildMessage($"unknown@{InboundDomain}");
        AddHeader(msg, "Received",
            $"from smtp.bank.cz (smtp.bank.cz [1.2.3.4]) by mx.fakvio.cz for {ActiveAlias}@{InboundDomain}; Fri, 01 Jan 2026 12:00:00 +0000");

        // Act
        var result = await _sut.ResolveAsync(msg, _master, InboundDomain);

        // Assert
        result.ShouldNotBeNull();
        result.MatchedAlias.ShouldBe(ActiveAlias);
        result.MatchedHeader.ShouldBe("Received-for");
    }

    [Fact]
    public async Task Resolve_PicksLastReceivedFor_WhenMultipleReceivedHeadersExist()
    {
        // When multiple Received: headers exist, the resolver should pick the
        // last one (the oldest / innermost hop) that has a "for" clause.
        // The first Received: (most recent hop) has a different address;
        // the last Received: has the actual recipient.
        var msg = BuildMessage($"unknown@{InboundDomain}");

        // Most-recent hop (prepended last by Fakvio's MX — not the original recipient)
        AddHeader(msg, "Received",
            $"from mx1.fakvio.cz by mx2.fakvio.cz for relay@{InboundDomain}; Fri, 01 Jan 2026 12:00:01 +0000");

        // Earlier hop (original inbound delivery — has the real alias)
        AddHeader(msg, "Received",
            $"from smtp.bank.cz by mx1.fakvio.cz for {ActiveAlias}@{InboundDomain}; Fri, 01 Jan 2026 12:00:00 +0000");

        // Act
        var result = await _sut.ResolveAsync(msg, _master, InboundDomain);

        // Assert: we should pick the last Received: with a "for" that resolves
        // to an active alias (AnotherActiveAlias would be "relay", but relay is unknown;
        // ActiveAlias is found in the last Received-for).
        result.ShouldNotBeNull();
        // The last Received: header seen that contains "for" and resolves to an active
        // alias is the one with ActiveAlias.
        result.MatchedAlias.ShouldBe(ActiveAlias);
        result.MatchedHeader.ShouldBe("Received-for");
    }

    [Fact]
    public async Task Resolve_SkipsRetiredAlias_AndContinuesChain()
    {
        // To: has a retired alias; Delivered-To has an active alias.
        // The router must NOT stop at the retired alias — it must continue
        // to the next candidate and eventually find the active Delivered-To.
        var msg = BuildMessage($"{RetiredAlias}@{InboundDomain}");
        AddHeader(msg, "Delivered-To", $"{ActiveAlias}@{InboundDomain}");

        // Act
        var result = await _sut.ResolveAsync(msg, _master, InboundDomain);

        // Assert: retired To: alias is skipped; active Delivered-To wins.
        result.ShouldNotBeNull();
        result.MatchedAlias.ShouldBe(ActiveAlias);
        result.MatchedHeader.ShouldBe("Delivered-To");
    }

    [Fact]
    public async Task Resolve_SkipsRetiredDeliveredTo_FallsBackToXOriginalTo()
    {
        // Delivered-To has a retired alias; X-Original-To has an active one.
        var msg = BuildMessage($"unknown@{InboundDomain}");
        AddHeader(msg, "Delivered-To", $"{RetiredAlias}@{InboundDomain}");
        AddHeader(msg, "X-Original-To", $"{ActiveAlias}@{InboundDomain}");

        // Act
        var result = await _sut.ResolveAsync(msg, _master, InboundDomain);

        // Assert
        result.ShouldNotBeNull();
        result.MatchedAlias.ShouldBe(ActiveAlias);
        result.MatchedHeader.ShouldBe("X-Original-To");
    }

    [Fact]
    public async Task Resolve_ReturnsNull_WhenNoCandidateMatches()
    {
        // All addresses in all headers are unknown — router must return null.
        var msg = BuildMessage($"unknown@{InboundDomain}");
        AddHeader(msg, "Delivered-To", $"also-unknown@{InboundDomain}");

        // Act
        var result = await _sut.ResolveAsync(msg, _master, InboundDomain);

        // Assert
        result.ShouldBeNull();
    }

    [Fact]
    public async Task Resolve_LogsAllCandidates_WhenUnrouted()
    {
        // Arrange: capture log messages via a mock logger.
        var logger = Substitute.For<ILogger<InboundAliasRouter>>();
        var sut = new InboundAliasRouter(logger);

        var msg = BuildMessage($"unknown@{InboundDomain}");
        AddHeader(msg, "Delivered-To", $"also-unknown@{InboundDomain}");

        // Act
        var result = await sut.ResolveAsync(msg, _master, InboundDomain);

        // Assert: null result expected (no active alias)
        result.ShouldBeNull();

        // The logger must have received at least one Information-level call that
        // mentions the candidate addresses (the exact wording may change, but the
        // key information must be logged).
        logger.Received().Log(
            LogLevel.Information,
            Arg.Any<EventId>(),
            Arg.Is<object>(o => o.ToString()!.Contains("also-unknown") ||
                                o.ToString()!.Contains("unknown")),
            Arg.Any<Exception?>(),
            Arg.Any<Func<object, Exception?, string>>());
    }

    [Fact]
    public async Task Resolve_IsCaseInsensitive_OnAlias()
    {
        // Alias lookup must be case-insensitive (email local-parts are technically
        // case-sensitive but in practice IMAP systems normalise them to lowercase).
        var msg = BuildMessage($"{ActiveAlias.ToUpperInvariant()}@{InboundDomain}");

        // Act
        var result = await _sut.ResolveAsync(msg, _master, InboundDomain);

        // Assert: ExtractLocalPart lower-cases the alias before DB lookup.
        result.ShouldNotBeNull();
        result.MatchedAlias.ShouldBe(ActiveAlias);
    }

    [Fact]
    public async Task Resolve_WithoutInboundDomain_SkipsDomainCheck()
    {
        // When inboundDomain is null the router should not filter by domain.
        var msg = BuildMessage($"{ActiveAlias}@any-domain.example.com");

        // Act — no inbound domain restriction
        var result = await _sut.ResolveAsync(msg, _master, inboundDomain: null);

        result.ShouldNotBeNull();
        result.MatchedAlias.ShouldBe(ActiveAlias);
    }

    // ─── ParseReceivedFor unit tests ──────────────────────────────────────────

    [Theory]
    [InlineData("from smtp.bank.cz by mx.fakvio.cz for pay-abc@fakvio.cz; Fri, 1 Jan 2026",
        "pay-abc@fakvio.cz")]
    [InlineData("from smtp.bank.cz by mx.fakvio.cz for <pay-abc@fakvio.cz>; Fri, 1 Jan 2026",
        "pay-abc@fakvio.cz")]
    [InlineData("no for keyword here", null)]
    [InlineData("from host by host for ; end", null)]
    public void ParseReceivedFor_ReturnsExpected(string header, string? expected)
    {
        var result = InboundAliasRouter.ParseReceivedFor(header);
        result.ShouldBe(expected);
    }

    [Fact]
    public void ExtractLocalPart_ReturnsEmptyForNull() =>
        InboundAliasRouter.ExtractLocalPart(null).ShouldBe(string.Empty);

    [Fact]
    public void ExtractLocalPart_ReturnsLowercaseLocalPart() =>
        InboundAliasRouter.ExtractLocalPart("Pay-ABC123@example.com").ShouldBe("pay-abc123");

    [Fact]
    public void ExtractLocalPart_ReturnsEmptyWhenNoAtSign() =>
        InboundAliasRouter.ExtractLocalPart("noemail").ShouldBe(string.Empty);
}

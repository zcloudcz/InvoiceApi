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

    // ─── X-Envelope-To header ─────────────────────────────────────────────────

    [Fact]
    public async Task Resolve_MatchesByXEnvelopeTo_WhenEnvelopeToAbsent()
    {
        // X-Envelope-To is the variant used by some sending providers when
        // Envelope-To is not present.
        var msg = BuildMessage($"forward@{InboundDomain}");
        AddHeader(msg, "X-Envelope-To", $"{ActiveAlias}@{InboundDomain}");

        var result = await _sut.ResolveAsync(msg, _master, InboundDomain);

        result.ShouldNotBeNull();
        result.MatchedAlias.ShouldBe(ActiveAlias);
        result.MatchedHeader.ShouldBe("X-Envelope-To");
    }

    // ─── Cc: header match ─────────────────────────────────────────────────────

    [Fact]
    public async Task Resolve_MatchesByCc_WhenNoOtherHeaderMatches()
    {
        // Cc: is the last-resort header. All earlier candidates are unknown.
        var msg = new MimeMessage();
        msg.Cc.Add(MailboxAddress.Parse($"{ActiveAlias}@{InboundDomain}"));

        var result = await _sut.ResolveAsync(msg, _master, InboundDomain);

        result.ShouldNotBeNull();
        result.MatchedAlias.ShouldBe(ActiveAlias);
        result.MatchedHeader.ShouldBe("Cc");
    }

    // ─── Multiple Delivered-To values ────────────────────────────────────────

    [Fact]
    public async Task Resolve_MultipleDeliveredTo_PicksFirstActiveAlias()
    {
        // When multiple Delivered-To headers are present the resolver should
        // return the first one that maps to an active alias.
        var msg = BuildMessage($"unknown@{InboundDomain}");
        AddHeader(msg, "Delivered-To", $"not-found@{InboundDomain}");          // unknown
        AddHeader(msg, "Delivered-To", $"{ActiveAlias}@{InboundDomain}");       // active
        AddHeader(msg, "Delivered-To", $"{AnotherActiveAlias}@{InboundDomain}"); // also active, but second

        var result = await _sut.ResolveAsync(msg, _master, InboundDomain);

        // The first active alias encountered wins.
        result.ShouldNotBeNull();
        result.MatchedAlias.ShouldBe(ActiveAlias);
        result.MatchedHeader.ShouldBe("Delivered-To");
    }

    // ─── Domain filter ────────────────────────────────────────────────────────

    [Fact]
    public async Task Resolve_SkipsCandidateWithWrongDomain_WhenInboundDomainSet()
    {
        // The active alias exists in the DB but the address in the header has
        // a different domain. With inboundDomain set the resolver must ignore it.
        var msg = BuildMessage($"{ActiveAlias}@other-domain.example.com");

        var result = await _sut.ResolveAsync(msg, _master, inboundDomain: InboundDomain);

        result.ShouldBeNull();
    }

    [Fact]
    public async Task Resolve_DomainCheckIsCaseInsensitive()
    {
        // Domain comparison must be case-insensitive.
        var msg = BuildMessage($"{ActiveAlias}@FAKVIO.CZ");

        var result = await _sut.ResolveAsync(msg, _master, inboundDomain: "fakvio.cz");

        result.ShouldNotBeNull();
        result.MatchedAlias.ShouldBe(ActiveAlias);
    }

    // ─── Empty message ────────────────────────────────────────────────────────

    [Fact]
    public async Task Resolve_ReturnsNull_WhenMessageHasNoRoutingHeaders()
    {
        // A completely bare message (no To, no Cc, no special headers) must not
        // throw — it should simply return null.
        var msg = new MimeMessage();

        var result = await _sut.ResolveAsync(msg, _master, InboundDomain);

        result.ShouldBeNull();
    }

    // ─── AliasResolution record integrity ────────────────────────────────────

    [Fact]
    public async Task Resolve_AliasResolution_ContainsCorrectMailboxId()
    {
        // MasterIndexEntry.TenantBankAccountMailboxId must be the seeded value
        // (10) so downstream code can open the correct tenant scope.
        var msg = BuildMessage($"{ActiveAlias}@{InboundDomain}");

        var result = await _sut.ResolveAsync(msg, _master, InboundDomain);

        result.ShouldNotBeNull();
        result.MasterIndexEntry.TenantBankAccountMailboxId.ShouldBe(10);
        result.MasterIndexEntry.TenantSchema.ShouldBe("tenant_1");
    }

    // ─── ParseReceivedFor — additional edge cases ─────────────────────────────

    [Theory]
    [InlineData("from smtp.bank.cz by mx.fakvio.cz FOR pay-abc@fakvio.cz; date",
        "pay-abc@fakvio.cz")]
    [InlineData("from smtp.bank.cz by mx.fakvio.cz For pay-abc@fakvio.cz; date",
        "pay-abc@fakvio.cz")]
    public void ParseReceivedFor_IsCaseInsensitiveOnForKeyword(string header, string expected)
    {
        // "FOR" and "For" must match the same as "for".
        InboundAliasRouter.ParseReceivedFor(header).ShouldBe(expected);
    }

    [Theory]
    [InlineData("from beforehand.example.com by mx.fakvio.cz; date")]  // "for" inside hostname
    [InlineData("from enforce.example.com by mx.fakvio.cz; date")]       // "for" inside word
    public void ParseReceivedFor_DoesNotMatchForInsideWord(string header)
    {
        // "for" that is a substring of a hostname or word must not trigger a match.
        InboundAliasRouter.ParseReceivedFor(header).ShouldBeNull();
    }

    // ─── CollectCandidates order and completeness ─────────────────────────────

    [Fact]
    public void CollectCandidates_ReturnsHeadersInSpecifiedFallbackOrder()
    {
        // Verify that the list comes back with Delivered-To before X-Original-To,
        // before Envelope-To, before To, before Cc — the fallback precedence
        // from issue #67 spec.
        var msg = new MimeMessage();
        msg.To.Add(MailboxAddress.Parse($"to@{InboundDomain}"));
        msg.Cc.Add(MailboxAddress.Parse($"cc@{InboundDomain}"));
        AddHeader(msg, "Delivered-To", $"delivered@{InboundDomain}");
        AddHeader(msg, "X-Original-To", $"xoriginal@{InboundDomain}");
        AddHeader(msg, "Envelope-To", $"envelope@{InboundDomain}");
        AddHeader(msg, "X-Envelope-To", $"xenvelope@{InboundDomain}");
        AddHeader(msg, "Received",
            $"from smtp.bank.cz by mx.fakvio.cz for received@{InboundDomain}; Fri, 1 Jan 2026");

        var candidates = InboundAliasRouter.CollectCandidates(msg);

        // Extract just the header labels in order.
        var headers = candidates.Select(c => c.Header).ToList();

        // Delivered-To must appear before X-Original-To
        headers.IndexOf("Delivered-To").ShouldBeLessThan(headers.IndexOf("X-Original-To"));
        // X-Original-To before Envelope-To
        headers.IndexOf("X-Original-To").ShouldBeLessThan(headers.IndexOf("Envelope-To"));
        // Envelope-To before X-Envelope-To
        headers.IndexOf("Envelope-To").ShouldBeLessThan(headers.IndexOf("X-Envelope-To"));
        // X-Envelope-To before Received-for
        headers.IndexOf("X-Envelope-To").ShouldBeLessThan(headers.IndexOf("Received-for"));
        // Received-for before To
        headers.IndexOf("Received-for").ShouldBeLessThan(headers.IndexOf("To"));
        // To before Cc
        headers.IndexOf("To").ShouldBeLessThan(headers.IndexOf("Cc"));
    }

    // ─── ExtractLastReceivedFor — direct unit tests ───────────────────────────

    [Fact]
    public void ExtractLastReceivedFor_ReturnsNull_WhenNoReceivedHeader()
    {
        var msg = BuildMessage($"any@{InboundDomain}");
        InboundAliasRouter.ExtractLastReceivedFor(msg).ShouldBeNull();
    }

    [Fact]
    public void ExtractLastReceivedFor_ReturnsLastMatchingValue_WhenMultipleReceived()
    {
        var msg = new MimeMessage();
        // First Received (most recent hop added last to list) — no "for"
        AddHeader(msg, "Received", "from mx1 by mx2; date");
        // Second Received — has a "for" clause
        AddHeader(msg, "Received",
            $"from smtp.bank.cz by mx1 for first@{InboundDomain}; date");
        // Third Received — also has a "for" clause (should override second)
        AddHeader(msg, "Received",
            $"from smtp2.bank.cz by mx1 for last@{InboundDomain}; date");

        var result = InboundAliasRouter.ExtractLastReceivedFor(msg);

        // The last Received: with a "for" clause wins.
        result.ShouldBe($"last@{InboundDomain}");
    }

    [Fact]
    public void ExtractLastReceivedFor_ReturnsNull_WhenReceivedHasNoForClause()
    {
        var msg = BuildMessage($"any@{InboundDomain}");
        AddHeader(msg, "Received", "from smtp.example.com by mx.fakvio.cz; Fri, 1 Jan 2026");

        InboundAliasRouter.ExtractLastReceivedFor(msg).ShouldBeNull();
    }
}

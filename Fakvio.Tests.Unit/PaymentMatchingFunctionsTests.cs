// ============================================================================
// PaymentMatchingFunctionsTests — Unit tests for the Azure Functions HTTP
// wrappers added in issue #6 (https://github.com/zcloudcz/InvoiceApi/issues/6).
//
// These wrappers were missing in the Functions host, which is why the Blazor
// UI's /payments page reported 404 NotFound on every request when pointed at
// the Functions deployment. The tests below verify the four pieces that the
// wrapper is responsible for:
//
//   1. Authorization gating  → 401 for anonymous, 403 for non-SysAdmin where required
//   2. Route parameter parsing → 400 on malformed long ids
//   3. Body deserialization    → controller receives the payload from req.Body
//   4. Controller delegation   → the underlying controller method is actually called
//
// The wrappers themselves are thin (controller resolved from DI, action invoked,
// response normalized), so we cover behaviour at the wrapper level without
// spinning up the whole Functions runtime — that pattern is consistent with the
// existing DiagnosticFunctionsTests style.
// ============================================================================

using System.Security.Claims;
using System.Text;
using System.Text.Json;
using Fakvio.API.Controller;
using Fakvio.Application.Service;
using Fakvio.Contracts.Common.Pagination;
using Fakvio.Contracts.Dto.PaymentMatching;
using Fakvio.Domain.Enums;
using Fakvio.Functions.Generated;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;
using NSubstitute;
using Shouldly;

namespace Fakvio.Tests.Unit;

/// <summary>
/// Tests for <see cref="PaymentMatchingFunctions"/> — tenant-facing wrappers around
/// <see cref="PaymentMatchingController"/>.
/// </summary>
public class PaymentMatchingFunctionsTests
{
    private readonly IBankAccountMailboxService _mailboxService = Substitute.For<IBankAccountMailboxService>();
    private readonly IBankTransactionQueryService _queryService = Substitute.For<IBankTransactionQueryService>();
    private readonly IPaymentMatchingService _matcher = Substitute.For<IPaymentMatchingService>();

    /// <summary>
    /// Builds the system-under-test wrapper, plus the controller it delegates to.
    /// Mirrors the DI setup used in production: the Functions wrapper takes a controller
    /// instance and reuses its action methods directly.
    /// </summary>
    private (PaymentMatchingFunctions Sut, PaymentMatchingController Controller) BuildSut()
    {
        var controller = new PaymentMatchingController(
            _mailboxService,
            _queryService,
            _matcher,
            Substitute.For<ILogger<PaymentMatchingController>>());

        var sut = new PaymentMatchingFunctions(controller);
        return (sut, controller);
    }

    /// <summary>
    /// Builds an HttpRequest with an authenticated ClaimsPrincipal so that
    /// the wrapper's IsAuthenticated check passes. Optionally adds the SysAdmin role.
    /// </summary>
    private static HttpRequest BuildAuthenticatedRequest(bool sysAdmin = false, string? jsonBody = null)
    {
        var ctx = new DefaultHttpContext();

        var claims = new List<Claim>
        {
            new("UserId", "42"),
            new(ClaimTypes.NameIdentifier, "42"),
        };
        if (sysAdmin)
            claims.Add(new Claim(ClaimTypes.Role, "SysAdmin"));

        var identity = new ClaimsIdentity(claims, authenticationType: "TestAuth");
        ctx.User = new ClaimsPrincipal(identity);

        if (jsonBody != null)
        {
            var bytes = Encoding.UTF8.GetBytes(jsonBody);
            ctx.Request.Body = new MemoryStream(bytes);
            ctx.Request.ContentType = "application/json";
            ctx.Request.ContentLength = bytes.Length;
        }

        return ctx.Request;
    }

    /// <summary>
    /// Builds an unauthenticated request (HttpContext.User has anonymous identity).
    /// </summary>
    private static HttpRequest BuildAnonymousRequest()
    {
        var ctx = new DefaultHttpContext();
        // Default user is anonymous (Identity.IsAuthenticated == false).
        return ctx.Request;
    }

    // ─── Authorization gating ───────────────────────────────────────────────

    [Fact]
    public async Task GetMailbox_Anonymous_Returns401()
    {
        var (sut, _) = BuildSut();
        var req = BuildAnonymousRequest();

        var result = await sut.PaymentMatching_GetMailbox(req, "1");

        result.ShouldBeOfType<UnauthorizedResult>();
    }

    [Fact]
    public async Task ListTransactions_Anonymous_Returns401()
    {
        var (sut, _) = BuildSut();
        var req = BuildAnonymousRequest();

        var result = await sut.PaymentMatching_ListTransactions(req);

        result.ShouldBeOfType<UnauthorizedResult>();
    }

    [Fact]
    public async Task UnmatchedCount_Anonymous_Returns401()
    {
        var (sut, _) = BuildSut();
        var req = BuildAnonymousRequest();

        var result = await sut.PaymentMatching_UnmatchedCount(req);

        result.ShouldBeOfType<UnauthorizedResult>();
    }

    // ─── Route parameter validation ────────────────────────────────────────

    [Fact]
    public async Task GetMailbox_InvalidBankAccountId_Returns400()
    {
        var (sut, _) = BuildSut();
        var req = BuildAuthenticatedRequest();

        var result = await sut.PaymentMatching_GetMailbox(req, "not-a-number");

        result.ShouldBeOfType<BadRequestObjectResult>();
    }

    [Fact]
    public async Task GetTransaction_InvalidId_Returns400()
    {
        var (sut, _) = BuildSut();
        var req = BuildAuthenticatedRequest();

        var result = await sut.PaymentMatching_GetTransaction(req, "abc");

        result.ShouldBeOfType<BadRequestObjectResult>();
    }

    // ─── Controller delegation ─────────────────────────────────────────────

    [Fact]
    public async Task GetMailbox_AuthenticatedUser_DelegatesToService()
    {
        // Arrange
        const long bankAccountId = 7L;
        var dto = new BankAccountMailboxDto
        {
            Id = 1,
            BankAccountId = bankAccountId,
            InboundAlias = "pay-abc",
            FullEmailAddress = "pay-abc@pay.fakvio.cz",
            IsActive = true,
        };
        _mailboxService.GetAsync(bankAccountId, Arg.Any<CancellationToken>()).Returns(dto);

        var (sut, _) = BuildSut();
        var req = BuildAuthenticatedRequest();

        // Act
        var result = await sut.PaymentMatching_GetMailbox(req, bankAccountId.ToString());

        // Assert: the underlying service was queried, and the response wraps the DTO.
        await _mailboxService.Received(1).GetAsync(bankAccountId, Arg.Any<CancellationToken>());
        var ok = result.ShouldBeOfType<OkObjectResult>();
        ok.Value.ShouldBe(dto);
    }

    [Fact]
    public async Task GetMailbox_UnknownBankAccount_Returns404()
    {
        // Arrange — service returns null → controller returns NotFound.
        _mailboxService.GetAsync(Arg.Any<long>(), Arg.Any<CancellationToken>())
            .Returns((BankAccountMailboxDto?)null);

        var (sut, _) = BuildSut();
        var req = BuildAuthenticatedRequest();

        // Act
        var result = await sut.PaymentMatching_GetMailbox(req, "999");

        // Assert
        result.ShouldBeOfType<NotFoundResult>();
    }

    [Fact]
    public async Task ListTransactions_PassesQueryParametersToService()
    {
        // Arrange
        var pagedResult = new PagedResult<BankTransactionDto>
        {
            Items = new List<BankTransactionDto>(),
            TotalCount = 0,
            PageNumber = 1,
            PageSize = 25,
        };
        _queryService.ListAsync(Arg.Any<BankTransactionFilterDto>(), Arg.Any<PaginationParams>(),
                Arg.Any<CancellationToken>())
            .Returns(pagedResult);

        var (sut, _) = BuildSut();
        var ctx = new DefaultHttpContext();
        ctx.User = new ClaimsPrincipal(new ClaimsIdentity(
            new[] { new Claim("UserId", "42") }, "TestAuth"));
        // Simulate the query string the Blazor UI sends for the Payments grid.
        ctx.Request.QueryString = new QueryString(
            "?status=Unmatched&direction=Incoming&bankAccountId=12&page=2&pageSize=25");

        // Act
        var result = await sut.PaymentMatching_ListTransactions(ctx.Request);

        // Assert
        await _queryService.Received(1).ListAsync(
            Arg.Is<BankTransactionFilterDto>(f =>
                f.Status == EMatchStatus.Unmatched &&
                f.Direction == EPaymentDirection.Incoming &&
                f.BankAccountId == 12L),
            Arg.Is<PaginationParams>(p => p.Page == 2 && p.PageSize == 25),
            Arg.Any<CancellationToken>());

        var ok = result.ShouldBeOfType<OkObjectResult>();
        ok.Value.ShouldBe(pagedResult);
    }

    [Fact]
    public async Task UnmatchedCount_DelegatesToService()
    {
        // Arrange
        _queryService.GetUnmatchedCountAsync(Arg.Any<CancellationToken>()).Returns(7);

        var (sut, _) = BuildSut();
        var req = BuildAuthenticatedRequest();

        // Act
        var result = await sut.PaymentMatching_UnmatchedCount(req);

        // Assert
        await _queryService.Received(1).GetUnmatchedCountAsync(Arg.Any<CancellationToken>());
        var ok = result.ShouldBeOfType<OkObjectResult>();
        ok.Value.ShouldBe(7);
    }

    // ─── Body deserialization ──────────────────────────────────────────────

    [Fact]
    public async Task Match_WithValidBody_DelegatesToMatcher()
    {
        // Arrange
        const long txId = 100L;
        var json = JsonSerializer.Serialize(new ManualMatchRequest
        {
            InvoiceId = 5,
            Amount = 1234m,
            Note = "ok",
        });
        _matcher.ManualMatchAsync(txId, 5L, 1234m, "ok", Arg.Any<long?>(), Arg.Any<CancellationToken>())
            .Returns(new ManualMatchResult(PaymentMatchId: 99, InvoicePaidAmount: 1234m, InvoiceRemaining: 0m));

        var (sut, _) = BuildSut();
        var req = BuildAuthenticatedRequest(jsonBody: json);

        // Act
        var result = await sut.PaymentMatching_Match(req, txId.ToString());

        // Assert
        await _matcher.Received(1).ManualMatchAsync(
            txId, 5L, 1234m, "ok", Arg.Any<long?>(), Arg.Any<CancellationToken>());

        var ok = result.ShouldBeOfType<OkObjectResult>();
        var response = ok.Value.ShouldBeOfType<ManualMatchResponse>();
        response.PaymentMatchId.ShouldBe(99);
        response.InvoicePaidAmount.ShouldBe(1234m);
    }

    [Fact]
    public async Task Match_EmptyBody_Returns400()
    {
        var (sut, _) = BuildSut();
        // Body is "null" (literal) → deserialized to null DTO → wrapper returns 400.
        var req = BuildAuthenticatedRequest(jsonBody: "null");

        var result = await sut.PaymentMatching_Match(req, "1");

        result.ShouldBeOfType<BadRequestObjectResult>();
    }

    [Fact]
    public async Task Ignore_AuthenticatedUser_DelegatesToMatcher()
    {
        // Arrange
        const long txId = 50L;
        var (sut, _) = BuildSut();
        var req = BuildAuthenticatedRequest();

        // Act
        var result = await sut.PaymentMatching_Ignore(req, txId.ToString());

        // Assert: controller returns NoContent (204) — kept as-is by Normalize.
        await _matcher.Received(1).IgnoreAsync(txId, Arg.Any<long?>(), Arg.Any<CancellationToken>());
        result.ShouldBeOfType<NoContentResult>();
    }

    [Fact]
    public async Task Unmatch_DelegatesToMatcher()
    {
        // Arrange
        const long txId = 200L;
        var json = JsonSerializer.Serialize(new UnmatchRequest
        {
            PaymentMatchId = 77,
            Reason = "wrong invoice",
        });

        var (sut, _) = BuildSut();
        var req = BuildAuthenticatedRequest(jsonBody: json);

        // Act
        var result = await sut.PaymentMatching_Unmatch(req, txId.ToString());

        // Assert
        await _matcher.Received(1).UnmatchAsync(77L, "wrong invoice", Arg.Any<long?>(), Arg.Any<CancellationToken>());
        result.ShouldBeOfType<NoContentResult>();
    }

    // ─── Mailbox lifecycle (Activate / Deactivate / Regenerate) ─────────────
    //
    // These three actions share the same wrapper shape (parse id → auth → call service)
    // so we cover each with: 401 anon, 400 bad-id, and 200 happy-path delegation.

    [Theory]
    [InlineData("activate")]
    [InlineData("deactivate")]
    [InlineData("regenerate")]
    public async Task MailboxLifecycle_Anonymous_Returns401(string action)
    {
        var (sut, _) = BuildSut();
        var req = BuildAnonymousRequest();

        var result = action switch
        {
            "activate"   => await sut.PaymentMatching_ActivateMailbox(req, "1"),
            "deactivate" => await sut.PaymentMatching_DeactivateMailbox(req, "1"),
            "regenerate" => await sut.PaymentMatching_RegenerateMailbox(req, "1"),
            _ => throw new ArgumentOutOfRangeException(nameof(action)),
        };

        result.ShouldBeOfType<UnauthorizedResult>();
    }

    [Theory]
    [InlineData("activate")]
    [InlineData("deactivate")]
    [InlineData("regenerate")]
    public async Task MailboxLifecycle_InvalidBankAccountId_Returns400(string action)
    {
        var (sut, _) = BuildSut();
        var req = BuildAuthenticatedRequest();

        var result = action switch
        {
            "activate"   => await sut.PaymentMatching_ActivateMailbox(req, "not-a-number"),
            "deactivate" => await sut.PaymentMatching_DeactivateMailbox(req, "not-a-number"),
            "regenerate" => await sut.PaymentMatching_RegenerateMailbox(req, "not-a-number"),
            _ => throw new ArgumentOutOfRangeException(nameof(action)),
        };

        result.ShouldBeOfType<BadRequestObjectResult>();
    }

    [Fact]
    public async Task ActivateMailbox_AuthenticatedUser_DelegatesToService()
    {
        // Arrange
        const long bankAccountId = 11L;
        var dto = new BankAccountMailboxDto
        {
            Id = 1,
            BankAccountId = bankAccountId,
            InboundAlias = "pay-new",
            FullEmailAddress = "pay-new@pay.fakvio.cz",
            IsActive = true,
        };
        _mailboxService.ActivateAsync(bankAccountId, Arg.Any<CancellationToken>()).Returns(dto);

        var (sut, _) = BuildSut();
        var req = BuildAuthenticatedRequest();

        // Act
        var result = await sut.PaymentMatching_ActivateMailbox(req, bankAccountId.ToString());

        // Assert
        await _mailboxService.Received(1).ActivateAsync(bankAccountId, Arg.Any<CancellationToken>());
        var ok = result.ShouldBeOfType<OkObjectResult>();
        ok.Value.ShouldBe(dto);
    }

    [Fact]
    public async Task DeactivateMailbox_AuthenticatedUser_DelegatesToService()
    {
        // Arrange
        const long bankAccountId = 12L;
        var dto = new BankAccountMailboxDto
        {
            Id = 2,
            BankAccountId = bankAccountId,
            InboundAlias = "pay-old",
            FullEmailAddress = "pay-old@pay.fakvio.cz",
            IsActive = false,
        };
        _mailboxService.DeactivateAsync(bankAccountId, Arg.Any<CancellationToken>()).Returns(dto);

        var (sut, _) = BuildSut();
        var req = BuildAuthenticatedRequest();

        // Act
        var result = await sut.PaymentMatching_DeactivateMailbox(req, bankAccountId.ToString());

        // Assert
        await _mailboxService.Received(1).DeactivateAsync(bankAccountId, Arg.Any<CancellationToken>());
        var ok = result.ShouldBeOfType<OkObjectResult>();
        ok.Value.ShouldBe(dto);
    }

    [Fact]
    public async Task RegenerateMailbox_AuthenticatedUser_DelegatesToService()
    {
        // Arrange — RegenerateAsync issues a brand-new alias; old one is preserved server-side.
        const long bankAccountId = 13L;
        var dto = new BankAccountMailboxDto
        {
            Id = 3,
            BankAccountId = bankAccountId,
            InboundAlias = "pay-fresh",
            FullEmailAddress = "pay-fresh@pay.fakvio.cz",
            IsActive = true,
        };
        _mailboxService.RegenerateAsync(bankAccountId, Arg.Any<CancellationToken>()).Returns(dto);

        var (sut, _) = BuildSut();
        var req = BuildAuthenticatedRequest();

        // Act
        var result = await sut.PaymentMatching_RegenerateMailbox(req, bankAccountId.ToString());

        // Assert
        await _mailboxService.Received(1).RegenerateAsync(bankAccountId, Arg.Any<CancellationToken>());
        var ok = result.ShouldBeOfType<OkObjectResult>();
        ok.Value.ShouldBe(dto);
    }

    // ─── Auth precedence on body-bearing endpoints ──────────────────────────
    //
    // Match/Unmatch deserialize the request body. Verifies that the auth check
    // runs BEFORE body deserialization — anonymous callers must be rejected even
    // when the body is malformed. Otherwise a malformed body could leak parser
    // exceptions to unauthenticated callers.

    [Fact]
    public async Task Match_AnonymousWithBody_Returns401()
    {
        var (sut, _) = BuildSut();
        // Body would deserialize fine, but auth must reject first.
        var ctx = new DefaultHttpContext();
        var bytes = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new ManualMatchRequest { InvoiceId = 1, Amount = 10m }));
        ctx.Request.Body = new MemoryStream(bytes);
        ctx.Request.ContentType = "application/json";
        ctx.Request.ContentLength = bytes.Length;

        var result = await sut.PaymentMatching_Match(ctx.Request, "1");

        result.ShouldBeOfType<UnauthorizedResult>();
        await _matcher.DidNotReceiveWithAnyArgs().ManualMatchAsync(
            default, default, default, default, default, default);
    }

    [Fact]
    public async Task Unmatch_AnonymousWithBody_Returns401()
    {
        var (sut, _) = BuildSut();
        var ctx = new DefaultHttpContext();
        var bytes = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new UnmatchRequest { PaymentMatchId = 1 }));
        ctx.Request.Body = new MemoryStream(bytes);
        ctx.Request.ContentType = "application/json";
        ctx.Request.ContentLength = bytes.Length;

        var result = await sut.PaymentMatching_Unmatch(ctx.Request, "1");

        result.ShouldBeOfType<UnauthorizedResult>();
        await _matcher.DidNotReceiveWithAnyArgs().UnmatchAsync(
            default, default, default, default);
    }

    [Fact]
    public async Task Ignore_Anonymous_Returns401()
    {
        var (sut, _) = BuildSut();
        var req = BuildAnonymousRequest();

        var result = await sut.PaymentMatching_Ignore(req, "1");

        result.ShouldBeOfType<UnauthorizedResult>();
        await _matcher.DidNotReceiveWithAnyArgs().IgnoreAsync(default, default, default);
    }
}

/// <summary>
/// Tests for <see cref="PaymentMatchingSysAdminFunctions"/> — SysAdmin-only wrappers.
/// Verifies role gating in addition to the standard auth + delegation flow.
/// </summary>
public class PaymentMatchingSysAdminFunctionsTests
{
    private readonly IPaymentMatchingSystemSettingsService _service =
        Substitute.For<IPaymentMatchingSystemSettingsService>();
    private readonly IImapPollService _pollService = Substitute.For<IImapPollService>();

    private (PaymentMatchingSysAdminFunctions Sut, PaymentMatchingSysAdminController Controller) BuildSut()
    {
        var controller = new PaymentMatchingSysAdminController(
            _service,
            _pollService,
            Substitute.For<ILogger<PaymentMatchingSysAdminController>>());

        var sut = new PaymentMatchingSysAdminFunctions(controller);
        return (sut, controller);
    }

    private static HttpRequest BuildRequest(bool authenticated, bool sysAdmin = false, string? jsonBody = null)
    {
        var ctx = new DefaultHttpContext();
        if (authenticated)
        {
            var claims = new List<Claim> { new("UserId", "42") };
            if (sysAdmin)
                claims.Add(new Claim(ClaimTypes.Role, "SysAdmin"));
            ctx.User = new ClaimsPrincipal(new ClaimsIdentity(claims, "TestAuth"));
        }

        if (jsonBody != null)
        {
            var bytes = Encoding.UTF8.GetBytes(jsonBody);
            ctx.Request.Body = new MemoryStream(bytes);
            ctx.Request.ContentType = "application/json";
            ctx.Request.ContentLength = bytes.Length;
        }

        return ctx.Request;
    }

    // ─── Authorization gating ───────────────────────────────────────────────

    [Fact]
    public async Task GetSettings_Anonymous_Returns401()
    {
        var (sut, _) = BuildSut();
        var req = BuildRequest(authenticated: false);

        var result = await sut.PaymentMatchingSysAdmin_GetSettings(req);

        result.ShouldBeOfType<UnauthorizedResult>();
    }

    [Fact]
    public async Task GetSettings_AuthenticatedNonSysAdmin_Returns403()
    {
        var (sut, _) = BuildSut();
        var req = BuildRequest(authenticated: true, sysAdmin: false);

        var result = await sut.PaymentMatchingSysAdmin_GetSettings(req);

        result.ShouldBeOfType<ForbidResult>();
    }

    [Fact]
    public async Task RunNow_NonSysAdmin_Returns403()
    {
        var (sut, _) = BuildSut();
        var req = BuildRequest(authenticated: true, sysAdmin: false);

        var result = await sut.PaymentMatchingSysAdmin_RunNow(req);

        result.ShouldBeOfType<ForbidResult>();
        // Service must NOT have been invoked because auth failed first.
        await _pollService.DidNotReceiveWithAnyArgs().RunCycleAsync(Arg.Any<CancellationToken>());
    }

    // ─── SysAdmin happy paths ──────────────────────────────────────────────

    [Fact]
    public async Task GetSettings_AsSysAdmin_DelegatesToService()
    {
        // Arrange
        var dto = new PaymentMatchingSystemSettingsDto
        {
            IsEnabled = true,
            ImapHost = "imap.test",
            PollIntervalMinutes = 30,
        };
        _service.GetAsync(Arg.Any<CancellationToken>()).Returns(dto);

        var (sut, _) = BuildSut();
        var req = BuildRequest(authenticated: true, sysAdmin: true);

        // Act
        var result = await sut.PaymentMatchingSysAdmin_GetSettings(req);

        // Assert
        await _service.Received(1).GetAsync(Arg.Any<CancellationToken>());
        var ok = result.ShouldBeOfType<OkObjectResult>();
        ok.Value.ShouldBe(dto);
    }

    [Fact]
    public async Task RunNow_AsSysAdmin_DelegatesToPollService()
    {
        // Arrange
        var cycle = new ImapPollCycleResult(Skipped: false, SkipReason: null,
            ProcessedCount: 3, IntervalMinutes: 30);
        _pollService.RunCycleAsync(Arg.Any<CancellationToken>()).Returns(cycle);

        var (sut, _) = BuildSut();
        var req = BuildRequest(authenticated: true, sysAdmin: true);

        // Act
        var result = await sut.PaymentMatchingSysAdmin_RunNow(req);

        // Assert
        await _pollService.Received(1).RunCycleAsync(Arg.Any<CancellationToken>());
        var ok = result.ShouldBeOfType<OkObjectResult>();
        ok.Value.ShouldBe(cycle);
    }

    [Fact]
    public async Task UpdateSettings_EmptyBody_Returns400()
    {
        var (sut, _) = BuildSut();
        // "null" literal → deserializer produces null → wrapper rejects with 400.
        var req = BuildRequest(authenticated: true, sysAdmin: true, jsonBody: "null");

        var result = await sut.PaymentMatchingSysAdmin_UpdateSettings(req);

        result.ShouldBeOfType<BadRequestObjectResult>();
        await _service.DidNotReceive().UpdateAsync(Arg.Any<PaymentMatchingSystemSettingsDto>(),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task UpdateSettings_Anonymous_Returns401()
    {
        var (sut, _) = BuildSut();
        var req = BuildRequest(authenticated: false, jsonBody: "{}");

        var result = await sut.PaymentMatchingSysAdmin_UpdateSettings(req);

        result.ShouldBeOfType<UnauthorizedResult>();
        // Body must NOT be deserialized for anonymous callers — auth check first.
        await _service.DidNotReceiveWithAnyArgs().UpdateAsync(default!, default);
    }

    [Fact]
    public async Task TestConnection_Anonymous_Returns401()
    {
        var (sut, _) = BuildSut();
        var req = BuildRequest(authenticated: false);

        var result = await sut.PaymentMatchingSysAdmin_TestConnection(req);

        result.ShouldBeOfType<UnauthorizedResult>();
    }

    [Fact]
    public async Task TestConnection_NonSysAdmin_Returns403()
    {
        var (sut, _) = BuildSut();
        var req = BuildRequest(authenticated: true, sysAdmin: false);

        var result = await sut.PaymentMatchingSysAdmin_TestConnection(req);

        result.ShouldBeOfType<ForbidResult>();
        await _service.DidNotReceiveWithAnyArgs().TestConnectionAsync(default!, default);
    }

    [Fact]
    public async Task TestConnection_AsSysAdmin_DelegatesToService()
    {
        // Arrange
        var input = new PaymentMatchingSystemSettingsDto
        {
            ImapHost = "imap.example.com",
            ImapPort = 993,
            ImapUsername = "pay@example.com",
            ImapPassword = "secret",
        };
        var json = JsonSerializer.Serialize(input);
        var expected = new TestImapConnectionResult { Success = true, MessageCount = 4 };
        _service.TestConnectionAsync(Arg.Any<PaymentMatchingSystemSettingsDto>(), Arg.Any<CancellationToken>())
            .Returns(expected);

        var (sut, _) = BuildSut();
        var req = BuildRequest(authenticated: true, sysAdmin: true, jsonBody: json);

        // Act
        var result = await sut.PaymentMatchingSysAdmin_TestConnection(req);

        // Assert
        await _service.Received(1).TestConnectionAsync(
            Arg.Is<PaymentMatchingSystemSettingsDto>(d => d.ImapHost == "imap.example.com"),
            Arg.Any<CancellationToken>());
        var ok = result.ShouldBeOfType<OkObjectResult>();
        ok.Value.ShouldBe(expected);
    }

    [Fact]
    public async Task TestConnection_EmptyBody_Returns400()
    {
        var (sut, _) = BuildSut();
        var req = BuildRequest(authenticated: true, sysAdmin: true, jsonBody: "null");

        var result = await sut.PaymentMatchingSysAdmin_TestConnection(req);

        result.ShouldBeOfType<BadRequestObjectResult>();
        await _service.DidNotReceiveWithAnyArgs().TestConnectionAsync(default!, default);
    }

    [Fact]
    public async Task RunNow_Anonymous_Returns401()
    {
        var (sut, _) = BuildSut();
        var req = BuildRequest(authenticated: false);

        var result = await sut.PaymentMatchingSysAdmin_RunNow(req);

        result.ShouldBeOfType<UnauthorizedResult>();
        await _pollService.DidNotReceiveWithAnyArgs().RunCycleAsync(default);
    }

    [Fact]
    public async Task UpdateSettings_ValidBody_DelegatesToService()
    {
        // Arrange
        var input = new PaymentMatchingSystemSettingsDto
        {
            IsEnabled = true,
            ImapHost = "imap.example.com",
            ImapPort = 993,
            ImapUsername = "pay@example.com",
            PollIntervalMinutes = 30,
        };
        var json = JsonSerializer.Serialize(input);

        var saved = new PaymentMatchingSystemSettingsDto
        {
            IsEnabled = true,
            ImapHost = "imap.example.com",
            PollIntervalMinutes = 30,
            // Password is never returned.
            ImapPassword = null,
        };
        _service.UpdateAsync(Arg.Any<PaymentMatchingSystemSettingsDto>(), Arg.Any<CancellationToken>())
            .Returns(saved);

        var (sut, _) = BuildSut();
        var req = BuildRequest(authenticated: true, sysAdmin: true, jsonBody: json);

        // Act
        var result = await sut.PaymentMatchingSysAdmin_UpdateSettings(req);

        // Assert
        await _service.Received(1).UpdateAsync(
            Arg.Is<PaymentMatchingSystemSettingsDto>(d =>
                d.ImapHost == "imap.example.com" && d.PollIntervalMinutes == 30),
            Arg.Any<CancellationToken>());

        var ok = result.ShouldBeOfType<OkObjectResult>();
        ok.Value.ShouldBe(saved);
    }
}

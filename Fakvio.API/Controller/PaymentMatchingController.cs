using System.Security.Claims;
using Fakvio.Application.Service;
using Fakvio.Contracts.Common.Pagination;
using Fakvio.Contracts.Dto.PaymentMatching;
using Fakvio.Domain.Enums;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Fakvio.API.Controller;

/// <summary>
/// Tenant-user endpoints for the payment matching feature:
///   - Bank account mailbox lifecycle (activate / deactivate / regenerate)
///   - Bank transaction grid (list / detail / match / unmatch / ignore)
///   - Dashboard counters
///
/// Requires authentication; SysAdmin endpoints live in
/// <see cref="PaymentMatchingSysAdminController"/>.
/// </summary>
[ApiController]
[Authorize]
[Route("api/payment-matching")]
[Produces("application/json")]
public class PaymentMatchingController : ControllerBase
{
    private readonly IBankAccountMailboxService _mailboxService;
    private readonly IBankTransactionQueryService _queryService;
    private readonly IPaymentMatchingService _matcher;
    private readonly ILogger<PaymentMatchingController> _logger;

    public PaymentMatchingController(
        IBankAccountMailboxService mailboxService,
        IBankTransactionQueryService queryService,
        IPaymentMatchingService matcher,
        ILogger<PaymentMatchingController> logger)
    {
        _mailboxService = mailboxService;
        _queryService = queryService;
        _matcher = matcher;
        _logger = logger;
    }

    // ─── Mailbox lifecycle ──────────────────────────────────────────────────

    /// <summary>Gets the mailbox for a bank account (or null if not yet created).</summary>
    [HttpGet("mailbox/{bankAccountId:long}")]
    [ProducesResponseType(typeof(BankAccountMailboxDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<BankAccountMailboxDto>> GetMailbox(
        long bankAccountId,
        CancellationToken ct = default)
    {
        var dto = await _mailboxService.GetAsync(bankAccountId, ct);
        return dto == null ? NotFound() : Ok(dto);
    }

    /// <summary>Activates auto-matching. Creates a new alias on first call, reactivates otherwise.</summary>
    [HttpPost("mailbox/{bankAccountId:long}/activate")]
    [ProducesResponseType(typeof(BankAccountMailboxDto), StatusCodes.Status200OK)]
    public async Task<ActionResult<BankAccountMailboxDto>> ActivateMailbox(
        long bankAccountId,
        CancellationToken ct = default)
    {
        var dto = await _mailboxService.ActivateAsync(bankAccountId, ct);
        return Ok(dto);
    }

    /// <summary>Turns off auto-matching. Alias is preserved for historical references.</summary>
    [HttpPost("mailbox/{bankAccountId:long}/deactivate")]
    [ProducesResponseType(typeof(BankAccountMailboxDto), StatusCodes.Status200OK)]
    public async Task<ActionResult<BankAccountMailboxDto>> DeactivateMailbox(
        long bankAccountId,
        CancellationToken ct = default)
    {
        var dto = await _mailboxService.DeactivateAsync(bankAccountId, ct);
        return Ok(dto);
    }

    /// <summary>Generates a new alias; the old one is retired but kept in audit.</summary>
    [HttpPost("mailbox/{bankAccountId:long}/regenerate")]
    [ProducesResponseType(typeof(BankAccountMailboxDto), StatusCodes.Status200OK)]
    public async Task<ActionResult<BankAccountMailboxDto>> RegenerateMailbox(
        long bankAccountId,
        CancellationToken ct = default)
    {
        var dto = await _mailboxService.RegenerateAsync(bankAccountId, ct);
        return Ok(dto);
    }

    // ─── Bank transactions grid ─────────────────────────────────────────────

    /// <summary>Paged list of bank transactions for the Payments grid.</summary>
    [HttpGet("transactions")]
    [ProducesResponseType(typeof(PagedResult<BankTransactionDto>), StatusCodes.Status200OK)]
    public async Task<ActionResult<PagedResult<BankTransactionDto>>> ListTransactions(
        [FromQuery] EMatchStatus? status,
        [FromQuery] EPaymentDirection? direction,
        [FromQuery] long? bankAccountId,
        [FromQuery] DateTime? from,
        [FromQuery] DateTime? to,
        [FromQuery] string? search,
        [FromQuery] int page = 1,
        [FromQuery] int pageSize = 50,
        CancellationToken ct = default)
    {
        var filter = new BankTransactionFilterDto
        {
            Status = status,
            Direction = direction,
            BankAccountId = bankAccountId,
            From = from,
            To = to,
            Search = search,
        };
        var paging = new PaginationParams { Page = page, PageSize = pageSize };

        var result = await _queryService.ListAsync(filter, paging, ct);
        return Ok(result);
    }

    /// <summary>Detail of a single transaction.</summary>
    [HttpGet("transactions/{id:long}")]
    [ProducesResponseType(typeof(BankTransactionDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<BankTransactionDto>> GetTransaction(
        long id,
        CancellationToken ct = default)
    {
        var dto = await _queryService.GetAsync(id, ct);
        return dto == null ? NotFound() : Ok(dto);
    }

    /// <summary>Manually links a transaction to a specific invoice (from the UI's dialog).</summary>
    [HttpPost("transactions/{id:long}/match")]
    [ProducesResponseType(typeof(ManualMatchResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public async Task<ActionResult<ManualMatchResponse>> Match(
        long id,
        [FromBody] ManualMatchRequest req,
        CancellationToken ct = default)
    {
        try
        {
            var result = await _matcher.ManualMatchAsync(
                bankTransactionId: id,
                invoiceId: req.InvoiceId,
                matchedAmount: req.Amount,
                note: req.Note,
                userId: GetUserId(),
                ct);

            return Ok(new ManualMatchResponse
            {
                PaymentMatchId = result.PaymentMatchId,
                InvoicePaidAmount = result.InvoicePaidAmount,
                InvoiceRemaining = result.InvoiceRemaining,
            });
        }
        catch (InvalidOperationException ex)
        {
            return BadRequest(new { message = ex.Message });
        }
        catch (ArgumentException ex)
        {
            return BadRequest(new { message = ex.Message });
        }
    }

    /// <summary>Removes an existing PaymentMatch row.</summary>
    [HttpPost("transactions/{id:long}/unmatch")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    public async Task<IActionResult> Unmatch(
        long id,
        [FromBody] UnmatchRequest req,
        CancellationToken ct = default)
    {
        await _matcher.UnmatchAsync(req.PaymentMatchId, req.Reason, GetUserId(), ct);
        return NoContent();
    }

    /// <summary>Marks a transaction as "not our concern" (ATM fees, personal transfers, …).</summary>
    [HttpPost("transactions/{id:long}/ignore")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    public async Task<IActionResult> Ignore(long id, CancellationToken ct = default)
    {
        await _matcher.IgnoreAsync(id, GetUserId(), ct);
        return NoContent();
    }

    // ─── Dashboard ──────────────────────────────────────────────────────────

    /// <summary>Count of unmatched/needs-review transactions for the nav badge.</summary>
    [HttpGet("unmatched-count")]
    [ProducesResponseType(typeof(int), StatusCodes.Status200OK)]
    public async Task<ActionResult<int>> UnmatchedCount(CancellationToken ct = default)
    {
        return Ok(await _queryService.GetUnmatchedCountAsync(ct));
    }

    // ─── Helpers ────────────────────────────────────────────────────────────

    /// <summary>Parses the "UserId" JWT claim. Returns null for missing/malformed.</summary>
    private long? GetUserId()
    {
        var raw = User.FindFirstValue("UserId");
        return long.TryParse(raw, out var id) ? id : null;
    }
}

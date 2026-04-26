using Fakvio.Application.Service;
using Fakvio.Domain.Entities;
using Fakvio.Infrastructure.Data;
using Microsoft.Azure.Functions.Worker;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Fakvio.Functions;

/// <summary>
/// Azure Functions timer triggers for the payment matching feature.
///
/// Why a Function alongside the BackgroundService in the API project? Azure Functions
/// Isolated Worker does NOT host <c>BackgroundService</c> reliably (it would only run
/// while a function is actively executing, and the consumption plan can scale to zero
/// between invocations). For Azure-hosted production deploys we therefore drive the
/// IMAP poll from a TimerTrigger here. The same <see cref="IImapPollService"/> is the
/// single source of truth for the cycle logic.
///
/// Scheduling strategy:
///   - <see cref="RunImapPoll"/> fires every 5 minutes (CRON "0 *​/5 * * * *").
///   - The cycle itself reads <c>PaymentMatchingSystemSettings.PollIntervalMinutes</c>
///     from the database and skips early if a previous cycle ran within that window.
///   - This gives us SysAdmin-controlled cadence (default 30 min) without redeploying.
///   - The PostgreSQL advisory lock inside <see cref="IImapPollService"/> guarantees
///     mutual exclusion across replicas and across this Function vs. the API's worker.
///
/// See <c>CLAUDE.md</c> "API + Functions duplication" for the deployment story.
/// </summary>
public class PaymentMatchingFunctions
{
    private readonly IImapPollService _pollService;
    private readonly MasterDbContext _master;
    private readonly ILogger<PaymentMatchingFunctions> _logger;

    public PaymentMatchingFunctions(
        IImapPollService pollService,
        MasterDbContext master,
        ILogger<PaymentMatchingFunctions> logger)
    {
        _pollService = pollService;
        _master = master;
        _logger = logger;
    }

    /// <summary>
    /// Periodic IMAP poll. Runs every 5 minutes; the cycle itself respects the
    /// configured <c>PollIntervalMinutes</c> (default 30) by checking the elapsed time
    /// since <c>LastRunAt</c> and skipping early when too soon.
    /// CRON format: {second} {minute} {hour} {day} {month} {dayOfWeek}
    /// </summary>
    [Function("RunImapPoll")]
    public async Task RunImapPoll([TimerTrigger("0 */5 * * * *")] TimerInfo timer, CancellationToken ct)
    {
        // Honour the SysAdmin-configured cadence by checking elapsed-since-last-run.
        // We do this BEFORE invoking the service so we don't acquire/release the
        // advisory lock just to discover the timer fired too early.
        PaymentMatchingSystemSettings? settings;
        try
        {
            settings = await _master.PaymentMatchingSystemSettings.AsNoTracking().FirstOrDefaultAsync(ct);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "RunImapPoll: cannot read settings — skipping cycle");
            return;
        }

        if (settings == null || !settings.IsEnabled)
        {
            _logger.LogDebug("RunImapPoll: feature disabled — skipping");
            return;
        }

        if (settings.LastRunAt is { } last)
        {
            var elapsed = DateTime.UtcNow - last;
            var threshold = TimeSpan.FromMinutes(settings.PollIntervalMinutes)
                            // 30s grace so a slightly-early Functions tick doesn't skip us forever.
                            - TimeSpan.FromSeconds(30);
            if (elapsed < threshold)
            {
                _logger.LogDebug(
                    "RunImapPoll: only {Elapsed:c} since last run (threshold {Threshold:c}) — skipping",
                    elapsed, threshold);
                return;
            }
        }

        var result = await _pollService.RunCycleAsync(ct);

        if (result.Skipped)
        {
            _logger.LogInformation("RunImapPoll: cycle skipped — {Reason}", result.SkipReason);
        }
        else
        {
            _logger.LogInformation(
                "RunImapPoll: cycle complete, processed {Count} emails", result.ProcessedCount);
        }
    }
}

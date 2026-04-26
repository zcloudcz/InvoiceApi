namespace Fakvio.Application.Service;

/// <summary>
/// Stateless service that executes a single IMAP poll cycle:
/// connects to the central mailbox, downloads new emails, routes each one
/// to its tenant via <see cref="Domain.Entities.MasterMailboxIndex"/>, and
/// hands the body to <see cref="IInboundEmailProcessor"/>.
///
/// Designed to be called from THREE places:
///   1. <c>ImapPollWorker</c> (BackgroundService in API host) — local dev + classic VM deploy.
///   2. <c>PaymentMatchingFunctions.RunImapPoll</c> (Azure Functions TimerTrigger) — production deploy.
///   3. <c>PaymentMatchingSysAdminController.RunNow</c> (HTTP) — SysAdmin manual trigger.
///
/// All three paths share the same advisory-lock-protected execution, so even if two
/// callers fire at the same time only one cycle actually runs.
/// </summary>
public interface IImapPollService
{
    /// <summary>
    /// Executes one poll cycle. Returns the configured interval (in minutes) so the
    /// caller can decide how long to wait before invoking it again. Always honours
    /// the master <c>IsEnabled</c> kill switch.
    /// </summary>
    Task<ImapPollCycleResult> RunCycleAsync(CancellationToken ct = default);
}

/// <summary>Aggregate outcome of one poll cycle.</summary>
public record ImapPollCycleResult(
    bool Skipped,
    string? SkipReason,
    int ProcessedCount,
    int IntervalMinutes);

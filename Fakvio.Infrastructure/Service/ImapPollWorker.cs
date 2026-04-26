using Fakvio.Application.Service;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Fakvio.Infrastructure.Service;

/// <summary>
/// BackgroundService wrapper around <see cref="IImapPollService"/> for hosts that
/// support <see cref="IHostedService"/> (the API project, classic VM deploy, local dev).
///
/// IMPORTANT: Azure Functions Isolated Worker does NOT host BackgroundServices reliably,
/// so for Functions deploys the same <see cref="IImapPollService"/> is invoked from
/// <c>PaymentMatchingFunctions.RunImapPoll</c> via [TimerTrigger].
/// See CLAUDE.md → "API + Functions duplication" for context.
///
/// Idle behaviour: the loop sleeps for the interval returned by
/// <see cref="IImapPollService.RunCycleAsync"/>, which mirrors
/// <see cref="Domain.Entities.PaymentMatchingSystemSettings.PollIntervalMinutes"/>.
/// SysAdmin changes to that value take effect on the next cycle.
/// </summary>
public class ImapPollWorker : BackgroundService
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<ImapPollWorker> _logger;

    public ImapPollWorker(IServiceScopeFactory scopeFactory, ILogger<ImapPollWorker> logger)
    {
        _scopeFactory = scopeFactory;
        _logger = logger;
    }

    /// <summary>Main loop — never exits until the process is stopped.</summary>
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("ImapPollWorker started");

        while (!stoppingToken.IsCancellationRequested)
        {
            int intervalMinutes = 30; // safe default if the cycle throws before reading settings
            try
            {
                using var scope = _scopeFactory.CreateScope();
                var pollService = scope.ServiceProvider.GetRequiredService<IImapPollService>();
                var result = await pollService.RunCycleAsync(stoppingToken);
                intervalMinutes = result.IntervalMinutes;
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "ImapPollWorker cycle failed");
            }

            try
            {
                await Task.Delay(TimeSpan.FromMinutes(intervalMinutes), stoppingToken);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }

        _logger.LogInformation("ImapPollWorker stopped");
    }
}

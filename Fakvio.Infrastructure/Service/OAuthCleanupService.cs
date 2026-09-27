using Fakvio.Domain.Entities;
using Fakvio.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Fakvio.Infrastructure.Service;

/// <summary>
/// Hourly sweep of expired/stale OAuth rows, per ADR 0001 (docs/adr/0001-mcp-oauth21.md) §4.3.
///
/// Follows the same single-class <see cref="BackgroundService"/> shape as
/// <c>LogCleanupService</c> (DEVGUIDE §6) rather than the stateless-service + thin-worker split
/// used by the per-tenant workers: there is nothing here to iterate per tenant (everything OAuth
/// lives in the master schema, like <see cref="Fakvio.Domain.Entities.ApiKey"/>), so the extra
/// abstraction would have no second caller.
///
/// One active client generates roughly 24 access tokens/day (hourly refresh cadence) — without
/// this sweep the <c>ApiKey</c>, <c>OAuthAuthorizationCode</c> and <c>OAuthRefreshToken</c> tables
/// grow forever.
///
/// Deletes (see ADR §4.3 "Úklid"):
/// - OAuth-issued <c>ApiKey</c> rows (<c>OAuthGrantId != null</c>) expired more than a day ago.
/// - Authorization codes older than a day (whether consumed or not — a 60s TTL row has no
///   reason to survive a day either way).
/// - Refresh tokens that are expired, OR consumed more than a day ago. The "keep consumed
///   tokens for a day" part is deliberate: <c>IOAuthService.RefreshAsync</c> needs a consumed
///   token to still be readable for a little while so it can tell "reused after rotation"
///   (revoke the grant) apart from "token that was cleaned up ages ago" — see Q5 in the ADR's
///   owner decisions for the 10-second concurrent-refresh grace window this interacts with.
/// - Grants revoked or past their absolute expiry for more than 90 days — the "connected apps"
///   list only needs to show live/recent history, not a permanent ledger.
/// </summary>
public class OAuthCleanupService : BackgroundService
{
    /// <summary>How often the cleanup job runs.</summary>
    internal static readonly TimeSpan CleanupInterval = TimeSpan.FromHours(1);

    /// <summary>How long an expired/consumed row is kept before deletion.</summary>
    internal static readonly TimeSpan ShortRetention = TimeSpan.FromDays(1);

    /// <summary>How long a revoked/expired grant is kept before deletion.</summary>
    internal static readonly TimeSpan GrantRetention = TimeSpan.FromDays(90);

    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<OAuthCleanupService> _logger;

    public OAuthCleanupService(IServiceScopeFactory scopeFactory, ILogger<OAuthCleanupService> logger)
    {
        _scopeFactory = scopeFactory;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("OAuthCleanupService started, running every {Interval}", CleanupInterval);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await RunOnceAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                // A failed sweep must not kill the loop — the next hourly tick tries again.
                _logger.LogError(ex, "OAuthCleanupService cycle failed");
            }

            try
            {
                await Task.Delay(CleanupInterval, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
        }

        _logger.LogInformation("OAuthCleanupService stopped");
    }

    /// <summary>One sweep. Internal so a unit/integration test can drive it without waiting an hour.</summary>
    internal async Task RunOnceAsync(CancellationToken ct)
    {
        using var scope = _scopeFactory.CreateScope();
        var context = scope.ServiceProvider.GetRequiredService<MasterDbContext>();

        var now = DateTime.UtcNow;
        var shortCutoff = now - ShortRetention;
        var grantCutoff = now - GrantRetention;

        var deletedAccessTokens = await context.ApiKey
            .Where(k => k.OAuthGrantId != null && k.ExpiresAt != null && k.ExpiresAt < shortCutoff)
            .ExecuteDeleteAsync(ct);

        var deletedCodes = await context.OAuthAuthorizationCode
            .Where(c => c.CreatedAt < shortCutoff)
            .ExecuteDeleteAsync(ct);

        var deletedRefreshTokens = await context.OAuthRefreshToken
            .Where(t => t.ExpiresAt < shortCutoff
                        || (t.ConsumedAt != null && t.ConsumedAt < shortCutoff))
            .ExecuteDeleteAsync(ct);

        var deletedGrants = await context.OAuthGrant
            .Where(g => (g.RevokedAt != null && g.RevokedAt < grantCutoff)
                        || g.ExpiresAt < grantCutoff)
            .ExecuteDeleteAsync(ct);

        if (deletedAccessTokens + deletedCodes + deletedRefreshTokens + deletedGrants > 0)
        {
            _logger.LogInformation(
                "OAuthCleanupService: deleted {AccessTokens} access tokens, {Codes} authorization codes, " +
                "{RefreshTokens} refresh tokens, {Grants} grants",
                deletedAccessTokens, deletedCodes, deletedRefreshTokens, deletedGrants);
        }
    }
}

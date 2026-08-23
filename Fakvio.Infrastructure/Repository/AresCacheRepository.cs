using AresService;
using Fakvio.Application.Service;
using Fakvio.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Fakvio.Infrastructure.Repository;

/// <summary>
/// Repository for managing ARES cache in database.
/// Implements caching logic with 30-day expiration.
///
/// Uses dual-context pattern: when a tenant is available (regular user or impersonating SysAdmin),
/// queries TenantDbContext. When no tenant context (SysAdmin without impersonation),
/// falls back to MasterDbContext. This prevents connection errors to non-existent
/// template databases when SysAdmin creates companies.
/// </summary>
public class AresCacheRepository : IAresCacheRepository
{
    private readonly TenantDbContext _tenantContext;
    private readonly MasterDbContext _masterContext;
    private readonly ITenantResolver _tenantResolver;
    private readonly ILogger<AresCacheRepository> _logger;

    /// <summary>
    /// How many expired rows one write may delete. Deletion is indexed (AresCache has an
    /// index on ExpiresAt) but the cap keeps a single lookup from paying for a huge
    /// backlog left by an earlier burst.
    /// </summary>
    private const int ExpiredSweepBatchSize = 200;

    /// <summary>
    /// True when no tenant is available — use MasterDbContext.
    /// False when a tenant is set — use TenantDbContext.
    /// </summary>
    private bool IsMasterContext => !_tenantResolver.GetCurrentCompanyId().HasValue;

    /// <summary>
    /// Returns the AresCache DbSet from the appropriate context (master or tenant).
    /// </summary>
    private DbSet<Domain.Entities.AresCache> CacheSet =>
        IsMasterContext ? _masterContext.AresCache : _tenantContext.AresCache;

    /// <summary>
    /// Returns the active DbContext for SaveChangesAsync calls.
    /// </summary>
    private DbContext ActiveContext =>
        IsMasterContext ? _masterContext : _tenantContext;

    public AresCacheRepository(
        TenantDbContext tenantContext,
        MasterDbContext masterContext,
        ITenantResolver tenantResolver,
        ILogger<AresCacheRepository> logger)
    {
        _tenantContext = tenantContext;
        _masterContext = masterContext;
        _tenantResolver = tenantResolver;
        _logger = logger;
    }

    /// <summary>
    /// Retrieves cached ARES data for given registration number.
    /// Returns null if not found.
    /// </summary>
    public async Task<AresCacheEntry?> GetCachedDataAsync(
        string registrationNumber,
        CancellationToken cancellationToken = default)
    {
        _logger.LogDebug("Looking up cache for IČO {RegistrationNumber} (context: {Context})",
            registrationNumber, IsMasterContext ? "Master" : "Tenant");

        var cached = await CacheSet
            .FirstOrDefaultAsync(
                x => x.RegistrationNumber == registrationNumber,
                cancellationToken);

        if (cached == null)
        {
            _logger.LogDebug("No cache found for IČO {RegistrationNumber}", registrationNumber);
            return null;
        }

        // Map domain entity to service model
        return new AresCacheEntry
        {
            RegistrationNumber = cached.RegistrationNumber,
            JsonData = cached.JsonData,
            FetchedAt = cached.FetchedAt,
            ExpiresAt = cached.ExpiresAt,
            IsSuccessful = cached.IsSuccessful,
            ErrorMessage = cached.ErrorMessage,
            CompanyName = cached.CompanyName,
            TaxNumber = cached.TaxNumber,
            IsVatPayer = cached.IsVatPayer
        };
    }

    /// <summary>
    /// Saves or updates ARES cache entry.
    /// If entry exists, it's updated; otherwise new entry is created.
    /// </summary>
    public async Task SaveCacheAsync(
        AresCacheEntry cacheEntry,
        CancellationToken cancellationToken = default)
    {
        _logger.LogInformation("Saving cache for IČO {RegistrationNumber} (context: {Context})",
            cacheEntry.RegistrationNumber, IsMasterContext ? "Master" : "Tenant");

        // Check if cache entry already exists
        var existing = await CacheSet
            .FirstOrDefaultAsync(
                x => x.RegistrationNumber == cacheEntry.RegistrationNumber,
                cancellationToken);

        if (existing != null)
        {
            // Update existing entry
            existing.JsonData = cacheEntry.JsonData;
            existing.FetchedAt = cacheEntry.FetchedAt;
            existing.ExpiresAt = cacheEntry.ExpiresAt;
            existing.IsSuccessful = cacheEntry.IsSuccessful;
            existing.ErrorMessage = cacheEntry.ErrorMessage;
            existing.CompanyName = cacheEntry.CompanyName;
            existing.TaxNumber = cacheEntry.TaxNumber;
            existing.IsVatPayer = cacheEntry.IsVatPayer;
            existing.UpdatedAt = DateTime.UtcNow;

            _logger.LogDebug("Updated existing cache entry for IČO {RegistrationNumber}", cacheEntry.RegistrationNumber);
        }
        else
        {
            // Create new entry
            var newCache = new Domain.Entities.AresCache
            {
                RegistrationNumber = cacheEntry.RegistrationNumber,
                JsonData = cacheEntry.JsonData,
                FetchedAt = cacheEntry.FetchedAt,
                ExpiresAt = cacheEntry.ExpiresAt,
                IsSuccessful = cacheEntry.IsSuccessful,
                ErrorMessage = cacheEntry.ErrorMessage,
                CompanyName = cacheEntry.CompanyName,
                TaxNumber = cacheEntry.TaxNumber,
                IsVatPayer = cacheEntry.IsVatPayer,
                CreatedAt = DateTime.UtcNow
            };

            await CacheSet.AddAsync(newCache, cancellationToken);

            _logger.LogDebug("Created new cache entry for IČO {RegistrationNumber}", cacheEntry.RegistrationNumber);
        }

        await RemoveExpiredEntriesAsync(cacheEntry.RegistrationNumber, cancellationToken);

        await ActiveContext.SaveChangesAsync(cancellationToken);

        _logger.LogInformation("Successfully saved cache for IČO {RegistrationNumber}", cacheEntry.RegistrationNumber);
    }

    /// <summary>
    /// Deletes cache rows whose TTL has passed (issue #200).
    ///
    /// WHY HERE AND NOT IN A SCHEDULED JOB
    /// Rows are only ever created by SaveCacheAsync, so sweeping on the write path means
    /// the table cannot grow while nothing writes to it — and it needs no BackgroundService
    /// plus [TimerTrigger] pair (see CLAUDE.md → "API + Functions duplication"), which
    /// would have to be duplicated for both hosts and would still run against every
    /// tenant schema separately. The caller pays for its own garbage: the anonymous
    /// endpoint that makes this table enumerable is also the one cleaning it up.
    ///
    /// The row being written is excluded — a refresh of an entry that has just expired
    /// must keep the new value, not delete it.
    ///
    /// The batch is capped so a single write never turns into an unbounded DELETE.
    /// ponytail: capped sweep on the write path; if the expired backlog ever outgrows
    /// the batch (it shrinks by one batch per write, grows by one row per write), move
    /// this to a scheduled job.
    /// </summary>
    private async Task RemoveExpiredEntriesAsync(
        string currentRegistrationNumber,
        CancellationToken cancellationToken)
    {
        var now = DateTime.UtcNow;

        var expired = await CacheSet
            .Where(x => x.ExpiresAt < now && x.RegistrationNumber != currentRegistrationNumber)
            .Take(ExpiredSweepBatchSize)
            .ToListAsync(cancellationToken);

        if (expired.Count == 0)
            return;

        CacheSet.RemoveRange(expired);

        _logger.LogInformation("Removed {Count} expired ARES cache entries (context: {Context})",
            expired.Count, IsMasterContext ? "Master" : "Tenant");
    }
}

using Fakvio.Domain.Entities;
using Fakvio.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Fakvio.Infrastructure.Service;

/// <summary>
/// Shared access to the single SystemConfiguration row in the master database.
///
/// SystemConfiguration is a singleton table created lazily on first write, so a fresh
/// install needs no seed migration. More than one service needs that behaviour
/// (<see cref="SystemConfigurationService"/>, <see cref="AiInstructionsService"/>), and
/// the default values must not be duplicated — otherwise the two copies drift apart.
/// </summary>
internal static class SystemConfigurationStore
{
    /// <summary>
    /// Returns the configuration row, or null when the table is still empty.
    /// Use this on read paths: a read must never write to the database.
    /// </summary>
    /// <remarks>
    /// OrderBy(Id) makes the pick deterministic and silences EF Core's warning about
    /// FirstOrDefault without an ordering.
    /// </remarks>
    public static Task<SystemConfiguration?> FindAsync(MasterDbContext context, CancellationToken ct)
        => context.Set<SystemConfiguration>().OrderBy(c => c.Id).FirstOrDefaultAsync(ct);

    /// <summary>
    /// Returns the configuration row, creating it with defaults when the table is empty.
    /// Use this on write paths only.
    /// </summary>
    public static async Task<SystemConfiguration> GetOrCreateAsync(
        MasterDbContext context,
        ILogger logger,
        CancellationToken ct)
    {
        var existing = await FindAsync(context, ct);
        if (existing != null)
            return existing;

        logger.LogInformation("No SystemConfiguration found — creating default row");
        var defaultConfig = new SystemConfiguration
        {
            SmtpHost = "",
            SmtpPort = 587,
            SmtpSenderEmail = "",
            SmtpSenderName = "Fakvio",
            SmtpUseSsl = true,
            JwtExpirationHours = 24,
            CreatedAt = DateTime.UtcNow
        };

        context.Set<SystemConfiguration>().Add(defaultConfig);
        await context.SaveChangesAsync(ct);
        return defaultConfig;
    }
}

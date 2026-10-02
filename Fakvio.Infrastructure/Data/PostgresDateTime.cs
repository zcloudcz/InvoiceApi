using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;

namespace Fakvio.Infrastructure.Data;

/// <summary>
/// Reinterprets a <c>DateTime</c> read back from PostgreSQL as UTC before it is compared
/// against <see cref="DateTime.UtcNow"/>.
///
/// This is not decoration. Both hosts switch on <c>Npgsql.EnableLegacyTimestampBehavior</c>
/// (Program.cs), under which a <c>timestamp with time zone</c> column is read back as a
/// DateTime <b>converted to the server's local time</b> with <see cref="DateTimeKind.Local"/>
/// instead of staying UTC. Comparing that value straight against <see cref="DateTime.UtcNow"/>
/// is off by the local UTC offset — and in any zone east of Greenwich it is off in the
/// dangerous direction: an expired credential keeps authenticating for another offset's worth
/// of hours (two, in CEST). First documented for <c>ApiKey.ExpiresAt</c> (issue #236,
/// <c>ApiKeyAuthenticator.ToUtc</c>) — every OAuth entity with an <c>ExpiresAt</c>/
/// <c>ConsumedAt</c>/<c>RevokedAt</c> column compared against "now" has the exact same
/// exposure (ADR 0001, docs/adr/0001-mcp-oauth21.md, T3/T5/T15), hence one shared helper
/// instead of re-deriving the fix per entity.
///
/// <c>Unspecified</c> is treated as UTC rather than converted, matching
/// <c>PostgresDateTime.UtcOnWrite</c>, which stamps every stored DateTime as UTC
/// without shifting it.
/// </summary>
public static class PostgresDateTime
{
    public static DateTime ToUtc(DateTime value) => value.Kind switch
    {
        DateTimeKind.Utc => value,
        DateTimeKind.Local => value.ToUniversalTime(),
        _ => DateTime.SpecifyKind(value, DateTimeKind.Utc)
    };

    /// <summary>Convenience overload for the many nullable <c>ExpiresAt</c>/<c>ConsumedAt</c>/<c>RevokedAt</c> columns.</summary>
    public static DateTime? ToUtc(DateTime? value) => value.HasValue ? ToUtc(value.Value) : null;

    /// <summary>
    /// Registers a write-side converter for every DateTime / DateTime? property of a context
    /// (call from <c>ConfigureConventions</c>). Npgsql 10 rejects non-UTC values for
    /// "timestamp with time zone", and Blazor date pickers send <c>Unspecified</c>. A SaveChanges
    /// hook cannot fix that (EF compares DateTime ignoring Kind, so assigning the same instant
    /// with a different Kind is a no-op) — a converter runs on every parameter, always.
    /// The converter is not part of the migration model, so it creates no migration.
    /// Reads are left as-is (see <see cref="ToUtc(DateTime)"/> for read-side normalization).
    /// </summary>
    public static void UtcOnWrite(ModelConfigurationBuilder builder) =>
        builder.Properties<DateTime>().HaveConversion<UtcWriteConverter>();

    private sealed class UtcWriteConverter : ValueConverter<DateTime, DateTime>
    {
        public UtcWriteConverter() : base(v => ToUtc(v), v => v) { }
    }
}

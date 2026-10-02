using Fakvio.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using Shouldly;

namespace Fakvio.Tests.Unit;

/// <summary>
/// Regression: the old SaveChanges hook only did <c>property.CurrentValue = SpecifyKind(dt, Utc)</c>,
/// but EF compares DateTime ignoring Kind, so it saw "no change" and kept the Unspecified value.
/// Npgsql 10 then throws for "timestamp with time zone". The fix is a model-wide value converter,
/// so every DateTime / DateTime? property in both contexts must carry one that stamps UTC on write.
/// (InMemory ignores converters, so we inspect the Npgsql model — no connection is opened.)
/// </summary>
public class DateTimeUtcConventionTests
{
    private static readonly DateTime Unspecified = new(2026, 3, 1, 10, 0, 0, DateTimeKind.Unspecified);

    [Fact]
    public void MasterModel_AllDateTimeProperties_StampUtcOnWrite()
    {
        using var ctx = new MasterDbContext(
            new DbContextOptionsBuilder<MasterDbContext>().UseNpgsql("Host=unused").Options);
        AssertAllDateTimesConvertToUtc(ctx);
    }

    [Fact]
    public void TenantModel_AllDateTimeProperties_StampUtcOnWrite()
    {
        using var ctx = new TenantDbContext(
            new DbContextOptionsBuilder<TenantDbContext>().UseNpgsql("Host=unused").Options);
        AssertAllDateTimesConvertToUtc(ctx);
    }

    private static void AssertAllDateTimesConvertToUtc(DbContext ctx)
    {
        var props = ctx.Model.GetEntityTypes()
            .SelectMany(e => e.GetProperties())
            .Where(p => (Nullable.GetUnderlyingType(p.ClrType) ?? p.ClrType) == typeof(DateTime))
            .ToList();
        props.ShouldNotBeEmpty();

        foreach (var p in props)
        {
            var converter = p.GetValueConverter();
            converter.ShouldNotBeNull($"{p.DeclaringType.DisplayName()}.{p.Name}");
            ((DateTime)converter!.ConvertToProvider(Unspecified)!).Kind.ShouldBe(DateTimeKind.Utc);
        }
    }
}

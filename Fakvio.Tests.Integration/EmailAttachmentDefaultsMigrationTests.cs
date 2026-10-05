using Fakvio.Domain.Enums;
using Fakvio.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Npgsql;
using Shouldly;

namespace Fakvio.Tests.Integration;

/// <summary>
/// The AddEmailAttachmentDefaults migrations (Master + Tenant) on a REAL PostgreSQL, in a disposable schema:
/// exactly three NOT NULL integer columns defaulting to 0 (= Inherit), so rows that existed before the migration
/// keep behaving as before. Skipped when FAKVIO_TEST_POSTGRES is not set (same convention as FeedbackMigrationTests).
/// </summary>
[Collection(RealPostgreSqlCollection.Name)]
public class EmailAttachmentDefaultsMigrationTests
{
    private const string MasterPrevious = "20261005085123_AddDocumentFileNameFormat";
    private const string TenantPrevious = "20261005085132_AddDocumentFileNameFormat";
    private static readonly string[] Columns = ["EmailAttachIsdoc", "EmailAttachPdf", "EmailAttachUbl"];

    [SkippableFact]
    public async Task Master_AddsThreeNotNullIntColumnsDefaultingToInherit_ForExistingRowsToo() =>
        await RunAsync(isTenant: false);

    [SkippableFact]
    public async Task Tenant_AddsThreeNotNullIntColumnsDefaultingToInherit_ForExistingRowsToo() =>
        await RunAsync(isTenant: true);

    private static async Task RunAsync(bool isTenant)
    {
        var connectionString = Environment.GetEnvironmentVariable("FAKVIO_TEST_POSTGRES");
        Skip.If(string.IsNullOrEmpty(connectionString), "Set FAKVIO_TEST_POSTGRES to a disposable PostgreSQL database.");
        AppContext.SetSwitch("Npgsql.EnableLegacyTimestampBehavior", true);

        var schema = "test_emailattach_" + Guid.NewGuid().ToString("N");
        await using var root = new NpgsqlConnection(connectionString);
        await root.OpenAsync();
        await Exec(root, $"CREATE SCHEMA \"{schema}\"");
        try
        {
            var scoped = new NpgsqlConnectionStringBuilder(connectionString) { SearchPath = schema }.ConnectionString;
            DbContext db = isTenant ? CreateTenant(scoped, schema) : CreateMaster(scoped);
            await using (db)
            {
                // 1. Schema as it was BEFORE this change, plus a legacy row (FK checks off: only BillingSettings is needed).
                await db.Database.GetService<Microsoft.EntityFrameworkCore.Migrations.IMigrator>()
                    .MigrateAsync(isTenant ? TenantPrevious : MasterPrevious);
                (await ColumnNames(root, schema)).ShouldNotContain("EmailAttachPdf");
                await Exec(root, "SET session_replication_role = replica");
                await Exec(root,
                    $"INSERT INTO \"{schema}\".\"BillingSettings\" (\"ClientId\", \"CreatedAt\", \"DueDateCalculationType\", \"DueDays\") " +
                    "VALUES (424242, now(), 1, 14)");
                await Exec(root, "SET session_replication_role = DEFAULT");

                // 2. Apply this migration.
                await db.Database.MigrateAsync();
                (await db.Database.GetAppliedMigrationsAsync()).ShouldContain(m => m.EndsWith("_AddEmailAttachmentDefaults"));

                // 3. Exactly the three columns, integer NOT NULL DEFAULT 0.
                var added = await ColumnNames(root, schema);
                added.Where(c => c.StartsWith("EmailAttach")).Order().ShouldBe(Columns);
                foreach (var column in Columns)
                {
                    await using var cmd = new NpgsqlCommand(
                        "SELECT data_type, is_nullable, column_default FROM information_schema.columns " +
                        "WHERE table_schema=@s AND table_name='BillingSettings' AND column_name=@c", root);
                    cmd.Parameters.AddWithValue("s", schema);
                    cmd.Parameters.AddWithValue("c", column);
                    await using var reader = await cmd.ExecuteReaderAsync();
                    (await reader.ReadAsync()).ShouldBeTrue();
                    reader.GetString(0).ShouldBe("integer");
                    reader.GetString(1).ShouldBe("NO");
                    reader.GetString(2).ShouldBe("0");
                }

                // 4. The legacy row reads as Inherit (0) - behaviour for existing clients is unchanged.
                await using var row = new NpgsqlCommand(
                    $"SELECT \"EmailAttachPdf\", \"EmailAttachIsdoc\", \"EmailAttachUbl\" FROM \"{schema}\".\"BillingSettings\" WHERE \"ClientId\"=424242", root);
                await using var r = await row.ExecuteReaderAsync();
                (await r.ReadAsync()).ShouldBeTrue();
                ((EEmailAttachMode)r.GetInt32(0)).ShouldBe(EEmailAttachMode.Inherit);
                ((EEmailAttachMode)r.GetInt32(1)).ShouldBe(EEmailAttachMode.Inherit);
                ((EEmailAttachMode)r.GetInt32(2)).ShouldBe(EEmailAttachMode.Inherit);
            }
        }
        finally
        {
            // The schema name is generated locally above, never supplied by a caller.
            await Exec(root, $"DROP SCHEMA \"{schema}\" CASCADE");
        }
    }

    private static MasterDbContext CreateMaster(string connectionString) =>
        new(new DbContextOptionsBuilder<MasterDbContext>().UseNpgsql(connectionString)
            .ConfigureWarnings(w => w.Ignore(RelationalEventId.PendingModelChangesWarning)).Options);

    private static TenantDbContext CreateTenant(string connectionString, string schema) =>
        new(new DbContextOptionsBuilder<TenantDbContext>()
            .UseNpgsql(connectionString, b =>
            {
                b.MigrationsAssembly("Fakvio.Infrastructure");
                b.MigrationsHistoryTable("__EFMigrationsHistory", schema);
            })
            .ReplaceService<Microsoft.EntityFrameworkCore.Infrastructure.IModelCacheKeyFactory, TenantModelCacheKeyFactory>()
            .ConfigureWarnings(w => w.Ignore(RelationalEventId.PendingModelChangesWarning)).Options)
        { Schema = schema };

    private static async Task<List<string>> ColumnNames(NpgsqlConnection connection, string schema)
    {
        await using var cmd = new NpgsqlCommand(
            "SELECT column_name FROM information_schema.columns WHERE table_schema=@s AND table_name='BillingSettings'", connection);
        cmd.Parameters.AddWithValue("s", schema);
        var names = new List<string>();
        await using var reader = await cmd.ExecuteReaderAsync();
        while (await reader.ReadAsync())
            names.Add(reader.GetString(0));
        return names;
    }

    private static async Task Exec(NpgsqlConnection connection, string sql)
    {
        await using var cmd = new NpgsqlCommand(sql, connection);
        await cmd.ExecuteNonQueryAsync();
    }
}

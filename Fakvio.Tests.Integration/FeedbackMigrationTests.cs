using Fakvio.Domain.Entities;
using Fakvio.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Npgsql;
using Shouldly;

namespace Fakvio.Tests.Integration;

/// <summary>Applies the generated migrations in a disposable schema and checks real PostgreSQL constraints.</summary>
public class FeedbackMigrationTests
{
    [SkippableFact]
    public async Task Migration_CreatesBoundedColumnsForeignKeysAndPagingIndexes()
    {
        var connectionString = Environment.GetEnvironmentVariable("FAKVIO_TEST_POSTGRES");
        Skip.If(string.IsNullOrEmpty(connectionString), "Set FAKVIO_TEST_POSTGRES to a disposable PostgreSQL database.");
        AppContext.SetSwitch("Npgsql.EnableLegacyTimestampBehavior", true);
        var schema = "test_feedback_" + Guid.NewGuid().ToString("N");
        await using var root = new NpgsqlConnection(connectionString);
        await root.OpenAsync();
        await using (var command = new NpgsqlCommand($"CREATE SCHEMA \"{schema}\"", root)) await command.ExecuteNonQueryAsync();
        try
        {
            var scoped = new NpgsqlConnectionStringBuilder(connectionString) { SearchPath = schema };
            var options = new DbContextOptionsBuilder<MasterDbContext>().UseNpgsql(scoped.ConnectionString)
                .ConfigureWarnings(w => w.Ignore(RelationalEventId.PendingModelChangesWarning)).Options;
            await using var db = new MasterDbContext(options);
            await db.Database.MigrateAsync();
            (await db.Database.GetAppliedMigrationsAsync()).ShouldContain("20261001105750_AddFeedbackReports");

            await using var columns = new NpgsqlCommand("SELECT character_maximum_length FROM information_schema.columns WHERE table_schema=@schema AND table_name='FeedbackReport' AND column_name='Subject'", root);
            columns.Parameters.AddWithValue("schema", schema);
            (await columns.ExecuteScalarAsync()).ShouldBe(200);
            await using var indexes = new NpgsqlCommand("SELECT indexname FROM pg_indexes WHERE schemaname=@schema AND tablename='FeedbackReport'", root);
            indexes.Parameters.AddWithValue("schema", schema);
            var names = new List<string>();
            await using (var reader = await indexes.ExecuteReaderAsync()) while (await reader.ReadAsync()) names.Add(reader.GetString(0));
            names.ShouldContain("IX_FeedbackReport_UserId_CompanyId_CreatedAt_Id");
            names.ShouldContain("IX_FeedbackReport_Status_CreatedAt_Id");
            names.ShouldContain("IX_FeedbackReport_CreatedAt_Id");

            db.Client.Add(new Client { Id = 99001, CompanyName = "Migration test", RegistrationNumber = "99999001", IsIssuer = true });
            db.User.Add(new User { Id = 99001, Email = "migration@feedback.test", CompanyId = 99001 });
            await db.SaveChangesAsync();
            db.FeedbackReport.Add(new FeedbackReport { UserId = 99001, CompanyId = 99001, Subject = new('x', 200), Description = new('x', 10000) });
            await db.SaveChangesAsync();
            db.ChangeTracker.Clear();
            db.FeedbackReport.Add(new FeedbackReport { UserId = 99001, CompanyId = 99001, Subject = new('x', 201), Description = "body" });
            var lengthError = await Should.ThrowAsync<DbUpdateException>(() => db.SaveChangesAsync());
            ((PostgresException)lengthError.InnerException!).SqlState.ShouldBe(PostgresErrorCodes.StringDataRightTruncation);
            db.ChangeTracker.Clear();
            db.FeedbackReport.Add(new FeedbackReport { UserId = 99999999, CompanyId = 99001, Subject = "Missing owner", Description = "body" });
            var ownerError = await Should.ThrowAsync<DbUpdateException>(() => db.SaveChangesAsync());
            ((PostgresException)ownerError.InnerException!).SqlState.ShouldBe(PostgresErrorCodes.ForeignKeyViolation);
        }
        finally
        {
            // The schema name is generated locally above, never supplied by a caller.
            await using var cleanup = new NpgsqlCommand($"DROP SCHEMA \"{schema}\" CASCADE", root);
            await cleanup.ExecuteNonQueryAsync();
        }
    }
}

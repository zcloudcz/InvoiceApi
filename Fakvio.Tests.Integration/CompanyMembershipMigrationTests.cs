using Fakvio.Domain.Entities;
using Fakvio.Domain.Enums;
using Fakvio.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql;
using Shouldly;

namespace Fakvio.Tests.Integration;

/// <summary>Exercises upgrade, safe rollback, backfill, and constraints on a disposable PostgreSQL schema.</summary>
[Collection(RealPostgreSqlCollection.Name)]
public class CompanyMembershipMigrationTests
{
    [SkippableFact]
    public async Task Upgrade_BackfillsLegacyGrants_AndRollbackCannotBroadenNewCredentials()
    {
        var connectionString = Environment.GetEnvironmentVariable("FAKVIO_TEST_POSTGRES");
        Skip.If(string.IsNullOrEmpty(connectionString), "Set FAKVIO_TEST_POSTGRES to a disposable PostgreSQL database.");
        AppContext.SetSwitch("Npgsql.EnableLegacyTimestampBehavior", true);
        var schema = "test_membership_" + Guid.NewGuid().ToString("N");
        await using var root = new NpgsqlConnection(connectionString);
        await root.OpenAsync();
        await using (var create = new NpgsqlCommand($"CREATE SCHEMA \"{schema}\"", root))
            await create.ExecuteNonQueryAsync();
        try
        {
            var scoped = new NpgsqlConnectionStringBuilder(connectionString) { SearchPath = schema };
            var options = new DbContextOptionsBuilder<MasterDbContext>().UseNpgsql(scoped.ConnectionString)
                .ConfigureWarnings(w => w.Ignore(RelationalEventId.PendingModelChangesWarning)).Options;
            await using var db = new MasterDbContext(options);
            await db.Database.MigrateAsync();
            db.Client.AddRange(
                new Client { Id = 99101, CompanyName = "Original company", RegistrationNumber = "99999101", IsIssuer = true },
                new Client { Id = 99102, CompanyName = "Second company", RegistrationNumber = "99999102", IsIssuer = true });
            db.User.AddRange(
                new User { Id = 99101, Email = "member@migration.test", CompanyId = 99101, Role = EUserRole.Admin },
                new User { Id = 99102, Email = "sysadmin@migration.test", Role = EUserRole.SysAdmin },
                new User { Id = 99103, Email = "revoked@migration.test", CompanyId = 99101, Role = EUserRole.User });
            await db.SaveChangesAsync();
            (await db.UserCompanyMembership.SingleAsync(m => m.UserId == 99103)).IsActive = false;
            db.UserCompanyMembership.Add(new UserCompanyMembership { UserId = 99101, CompanyId = 99102, Role = EUserRole.User });
            db.OAuthGrant.Add(new OAuthGrant { Id = 99101, UserId = 99101, CompanyId = 99101, ExpiresAt = DateTime.UtcNow.AddDays(1) });
            await db.SaveChangesAsync();
            db.ApiKey.AddRange(
                new ApiKey { UserId = 99101, Name = "Legacy default", KeyHash = "legacy", CompanyId = 99101, AllowedCompanyIds = [99101], Scopes = "read" },
                new ApiKey { UserId = 99101, Name = "Secondary only", KeyHash = "secondary", CompanyId = 99102, AllowedCompanyIds = [99102], Scopes = "read" },
                new ApiKey { UserId = 99102, Name = "Platform", KeyHash = "platform", Scopes = "read" },
                new ApiKey { UserId = 99101, Name = "OAuth", KeyHash = "oauth", CompanyId = 99101, AllowedCompanyIds = [99101], OAuthGrantId = 99101, Scopes = "read" });
            db.OAuthAuthorizationCode.Add(new OAuthAuthorizationCode
            {
                UserId = 99101, CompanyId = 99101, CodeHash = "original-code", ExpiresAt = DateTime.UtcNow.AddMinutes(1)
            });
            db.OAuthAuthorizationCode.Add(new OAuthAuthorizationCode
            {
                UserId = 99103, CompanyId = 99101, CodeHash = "revoked-membership-code", ExpiresAt = DateTime.UtcNow.AddMinutes(1)
            });
            await db.SaveChangesAsync();
            db.ChangeTracker.Clear();

            // Returning to the previous schema leaves real legacy-shaped rows to upgrade.
            // A secondary-only credential must be revoked before its company binding disappears.
            await db.GetService<IMigrator>().MigrateAsync("20261001105750_AddFeedbackReports");
            await db.Database.MigrateAsync();

            var membership = await db.UserCompanyMembership.SingleAsync(m => m.UserId == 99101);
            membership.CompanyId.ShouldBe(99101);
            membership.Role.ShouldBe(EUserRole.Admin);
            (await db.UserCompanyMembership.AnyAsync(m => m.UserId == 99102)).ShouldBeFalse();
            var keys = await db.ApiKey.ToDictionaryAsync(k => k.KeyHash);
            keys["legacy"].CompanyId.ShouldBe(99101);
            keys["legacy"].AllowedCompanyIds.ShouldBe(new long[] { 99101 });
            keys["legacy"].RevokedAt.ShouldBeNull();
            keys["secondary"].RevokedAt.ShouldNotBeNull();
            keys["platform"].CompanyId.ShouldBeNull();
            keys["platform"].AllowedCompanyIds.ShouldBeEmpty();
            keys["platform"].RevokedAt.ShouldBeNull();
            keys["oauth"].CompanyId.ShouldBe(99101);
            keys["oauth"].AllowedCompanyIds.ShouldBe(new long[] { 99101 });
            (await db.OAuthGrant.SingleAsync()).CompanyId.ShouldBe(99101);
            (await db.OAuthAuthorizationCode.SingleAsync()).CompanyId.ShouldBe(99101);
            (await db.User.SingleAsync(u => u.Id == 99101)).CompanyId.ShouldBe(99101);

            db.ChangeTracker.Clear();
            db.UserCompanyMembership.Add(new UserCompanyMembership { UserId = 99101, CompanyId = 99101, Role = EUserRole.User });
            var duplicate = await Should.ThrowAsync<DbUpdateException>(() => db.SaveChangesAsync());
            ((PostgresException)duplicate.InnerException!).SqlState.ShouldBe(PostgresErrorCodes.UniqueViolation);
            db.ChangeTracker.Clear();
            db.UserCompanyMembership.Add(new UserCompanyMembership { UserId = 99101, CompanyId = 99102, Role = EUserRole.SysAdmin });
            var invalidRole = await Should.ThrowAsync<DbUpdateException>(() => db.SaveChangesAsync());
            ((PostgresException)invalidRole.InnerException!).SqlState.ShouldBe(PostgresErrorCodes.CheckViolation);
        }
        finally
        {
            // The schema name is generated locally; this never targets an existing tenant.
            await using var cleanup = new NpgsqlCommand($"DROP SCHEMA \"{schema}\" CASCADE", root);
            await cleanup.ExecuteNonQueryAsync();
        }
    }
}

using System.ComponentModel.DataAnnotations;
using System.Security.Cryptography;
using System.Text;
using Fakvio.Application.Service;
using Fakvio.Domain.Entities;
using Fakvio.Domain.Enums;
using Fakvio.Infrastructure.Data;
using Fakvio.Infrastructure.Service;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;
using NSubstitute;
using Shouldly;

namespace Fakvio.Tests.Integration;

[Collection(RealPostgreSqlCollection.Name)]
public class CompanyInvitationConcurrencyTests
{
    [SkippableFact]
    public async Task RevocationRacingAcceptanceLeavesMembershipRevoked()
    {
        var connectionString = Environment.GetEnvironmentVariable("FAKVIO_TEST_POSTGRES");
        Skip.If(string.IsNullOrEmpty(connectionString), "Set FAKVIO_TEST_POSTGRES to a disposable PostgreSQL database.");
        AppContext.SetSwitch("Npgsql.EnableLegacyTimestampBehavior", true);
        var schema = "test_revoke_" + Guid.NewGuid().ToString("N");
        await using var connection = new NpgsqlConnection(connectionString); await connection.OpenAsync();
        await using (var command = new NpgsqlCommand($"CREATE SCHEMA \"{schema}\"", connection)) await command.ExecuteNonQueryAsync();
        try
        {
            var scoped = new NpgsqlConnectionStringBuilder(connectionString) { SearchPath = schema };
            var options = new DbContextOptionsBuilder<MasterDbContext>().UseNpgsql(scoped.ConnectionString)
                .ConfigureWarnings(w => w.Ignore(RelationalEventId.PendingModelChangesWarning)).Options;
            const string token = "revocation-race-token";
            await using (var seed = new MasterDbContext(options))
            {
                await seed.Database.MigrateAsync();
                seed.Client.Add(new Client { Id = 99501, CompanyName = "Company", RegistrationNumber = "99999501", IsIssuer = true });
                seed.User.AddRange(new User { Id = 99501, Email = "member@revoke.test", Role = EUserRole.User },
                    new User { Id = 99502, Email = "admin@revoke.test", Role = EUserRole.SysAdmin });
                await seed.SaveChangesAsync();
                seed.UserCompanyMembership.Add(new() { UserId = 99501, CompanyId = 99501, IsActive = false, Role = EUserRole.User });
                seed.CompanyMembershipInvitation.Add(new() { UserId = 99501, CompanyId = 99501, Role = EUserRole.Admin,
                    TokenHash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(token))), ExpiresAt = DateTime.UtcNow.AddMinutes(10) });
                await seed.SaveChangesAsync();
            }
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            var ready = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var started = 0;
            async Task Run(bool revoke)
            {
                await using var db = new MasterDbContext(options);
                var user = Substitute.For<ICurrentUserService>(); user.GetCurrentUserId().Returns(revoke ? 99502 : 99501);
                var tenant = Substitute.For<ITenantResolver>(); tenant.IsSysAdmin().Returns(revoke);
                var service = new CompanyMembershipService(db, user, tenant, Substitute.For<ITenantProvisioningService>(),
                    new HttpContextAccessor { HttpContext = new DefaultHttpContext() }, NullLogger<CompanyMembershipService>.Instance);
                if (Interlocked.Increment(ref started) == 2) ready.SetResult();
                await ready.Task.WaitAsync(deadline.Token);
                if (revoke) await service.UpdateForUserAsync(99501, 99501, new() { Role = EUserRole.User, IsActive = false }, deadline.Token);
                else
                {
                    try { await service.AcceptAsync(token, deadline.Token); }
                    catch (ValidationException) { /* Revocation consumed the token first. */ }
                }
            }
            await Task.WhenAll(Run(true), Run(false));
            await using var verify = new MasterDbContext(options);
            var membership = await verify.UserCompanyMembership.SingleAsync(m => m.UserId == 99501);
            membership.IsActive.ShouldBeFalse(); membership.Role.ShouldBe(EUserRole.User);
            (await verify.CompanyMembershipInvitation.SingleAsync()).ConsumedAt.ShouldNotBeNull();
        }
        finally
        {
            await using var cleanup = new NpgsqlCommand($"DROP SCHEMA \"{schema}\" CASCADE", connection);
            await cleanup.ExecuteNonQueryAsync();
        }
    }

    [SkippableFact]
    public async Task SameInvitation_ConcurrentAcceptanceHasOneWinnerAndOneMembership()
    {
        var connectionString = Environment.GetEnvironmentVariable("FAKVIO_TEST_POSTGRES");
        Skip.If(string.IsNullOrEmpty(connectionString), "Set FAKVIO_TEST_POSTGRES to a disposable PostgreSQL database.");
        AppContext.SetSwitch("Npgsql.EnableLegacyTimestampBehavior", true);
        var schema = "test_invitation_" + Guid.NewGuid().ToString("N");
        await using var connection = new NpgsqlConnection(connectionString); await connection.OpenAsync();
        await using (var command = new NpgsqlCommand($"CREATE SCHEMA \"{schema}\"", connection)) await command.ExecuteNonQueryAsync();
        try
        {
            var scoped = new NpgsqlConnectionStringBuilder(connectionString) { SearchPath = schema };
            var options = new DbContextOptionsBuilder<MasterDbContext>().UseNpgsql(scoped.ConnectionString)
                .ConfigureWarnings(w => w.Ignore(RelationalEventId.PendingModelChangesWarning)).Options;
            const string token = "test-invitation-secret";
            await using (var seed = new MasterDbContext(options))
            {
                await seed.Database.MigrateAsync();
                seed.Client.Add(new Client { Id = 99401, CompanyName = "Invitation company", RegistrationNumber = "99999401", IsIssuer = true });
                seed.User.Add(new User { Id = 99401, Email = "invitee@concurrency.test", PasswordHash = "unchanged", Role = EUserRole.User });
                await seed.SaveChangesAsync();
                seed.CompanyMembershipInvitation.Add(new CompanyMembershipInvitation { UserId = 99401, CompanyId = 99401,
                    Role = EUserRole.Admin, TokenHash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(token))), ExpiresAt = DateTime.UtcNow.AddMinutes(10) });
                await seed.SaveChangesAsync();
            }
            var ready = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var started = 0;
            async Task<bool> Accept()
            {
                await using var db = new MasterDbContext(options);
                var user = Substitute.For<ICurrentUserService>(); user.GetCurrentUserId().Returns(99401);
                var service = new CompanyMembershipService(db, user, Substitute.For<ITenantResolver>(), Substitute.For<ITenantProvisioningService>(),
                    new HttpContextAccessor { HttpContext = new DefaultHttpContext() }, NullLogger<CompanyMembershipService>.Instance);
                if (Interlocked.Increment(ref started) == 2) ready.SetResult();
                await ready.Task;
                try { await service.AcceptAsync(token); return true; }
                catch (ValidationException) { return false; }
            }
            var results = await Task.WhenAll(Accept(), Accept());
            results.Count(x => x).ShouldBe(1);
            await using var verify = new MasterDbContext(options);
            (await verify.UserCompanyMembership.CountAsync(m => m.UserId == 99401)).ShouldBe(1);
            (await verify.CompanyMembershipInvitation.SingleAsync()).ConsumedAt.ShouldNotBeNull();
            (await verify.User.SingleAsync(u => u.Id == 99401)).PasswordHash.ShouldBe("unchanged");
        }
        finally
        {
            await using var cleanup = new NpgsqlCommand($"DROP SCHEMA \"{schema}\" CASCADE", connection);
            await cleanup.ExecuteNonQueryAsync();
        }
    }
}

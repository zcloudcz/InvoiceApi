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

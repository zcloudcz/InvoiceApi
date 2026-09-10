using Fakvio.Application.Service;
using Fakvio.Domain.Entities;
using Fakvio.Infrastructure.Data;
using Fakvio.Infrastructure.Service;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using NSubstitute;
using Shouldly;

namespace Fakvio.Tests.Unit;

/// <summary>
/// Tests for the daily dunning worker that replaced the Functions timer trigger.
/// Two things matter: the sleep is computed towards the next 06:00 UTC (not "in 24 h"),
/// and one tenant's failure does not stop the others.
/// </summary>
public class ReminderWorkerTests
{
    [Theory]
    [InlineData("2026-09-10T05:00:00Z", 1.0)]   // before the slot → today, in one hour
    [InlineData("2026-09-10T06:00:00Z", 24.0)]  // exactly on the slot → it just fired, wait a day
    [InlineData("2026-09-10T18:30:00Z", 11.5)]  // after the slot → tomorrow
    public void DelayUntil_TargetsNextSixAmUtc(string nowIso, double expectedHours)
    {
        var now = DateTime.Parse(nowIso, null, System.Globalization.DateTimeStyles.AdjustToUniversal);

        var delay = ReminderWorker.DelayUntil(now, ReminderWorker.RunAtUtc);

        delay.ShouldBe(TimeSpan.FromHours(expectedHours));
    }

    [Fact]
    public async Task RunOnceAsync_FirstTenantThrows_SecondTenantStillProcessed()
    {
        // Arrange — two provisioned tenants in the master DB; the reminder service throws for
        // the first and succeeds for the second.
        var dbName = Guid.NewGuid().ToString();
        var reminderService = Substitute.For<IReminderService>();
        reminderService.ProcessOverdueInvoicesAsync(Arg.Any<CancellationToken>())
            .Returns(
                _ => Task.FromException<int>(new InvalidOperationException("tenant 1 is broken")),
                _ => Task.FromResult(3));

        var services = new ServiceCollection();
        services.AddLogging();
        services.AddDbContext<MasterDbContext>(o => o.UseInMemoryDatabase(dbName));
        services.AddScoped<ITenantDbContextFactory>(_ => Substitute.For<ITenantDbContextFactory>());
        services.AddSingleton(reminderService);
        using var provider = services.BuildServiceProvider();

        using (var seed = provider.CreateScope())
        {
            var master = seed.ServiceProvider.GetRequiredService<MasterDbContext>();
            master.CompanySystemSettings.AddRange(
                new CompanySystemSettings { CompanyId = 1, SchemaName = "tenant_1", IsProvisioned = true, IsActive = true },
                new CompanySystemSettings { CompanyId = 2, SchemaName = "tenant_2", IsProvisioned = true, IsActive = true },
                // Not provisioned → must be skipped entirely.
                new CompanySystemSettings { CompanyId = 3, SchemaName = "tenant_3", IsProvisioned = false, IsActive = true });
            await master.SaveChangesAsync();
        }

        var worker = new ReminderWorker(
            provider.GetRequiredService<IServiceScopeFactory>(),
            Substitute.For<ILogger<ReminderWorker>>());

        // Act — must not throw even though tenant 1 did.
        await worker.RunOnceAsync(CancellationToken.None);

        // Assert — both provisioned tenants were attempted, the unprovisioned one was not.
        await reminderService.Received(2).ProcessOverdueInvoicesAsync(Arg.Any<CancellationToken>());
    }
}

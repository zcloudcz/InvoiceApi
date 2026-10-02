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
/// Tests for the hourly recurring-invoice worker. Mirrors ReminderWorkerTests: one tenant's
/// failure must not stop the others, and unprovisioned tenants are skipped entirely.
/// </summary>
public class RecurringInvoiceWorkerTests
{
    [Fact]
    public async Task RunOnceAsync_FirstTenantThrows_SecondTenantStillProcessed()
    {
        var dbName = Guid.NewGuid().ToString();
        var recurringInvoiceService = Substitute.For<IRecurringInvoiceService>();
        recurringInvoiceService
            .RunCycleAsync(Arg.Any<long>(), Arg.Any<DateTimeOffset>(), Arg.Any<CancellationToken>())
            .Returns(
                _ => Task.FromException<int>(new InvalidOperationException("tenant 1 is broken")),
                _ => Task.FromResult(2));

        var services = new ServiceCollection();
        services.AddLogging();
        services.AddDbContext<MasterDbContext>(o => o.UseInMemoryDatabase(dbName));
        services.AddTenantScopeStubs();
        services.AddSingleton(recurringInvoiceService);
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

        var worker = new RecurringInvoiceWorker(
            provider.GetRequiredService<IServiceScopeFactory>(),
            Substitute.For<ILogger<RecurringInvoiceWorker>>());

        // Act — must not throw even though tenant 1 did.
        await worker.RunOnceAsync(CancellationToken.None);

        // Assert — both provisioned tenants were attempted, the unprovisioned one was not.
        await recurringInvoiceService.Received(2)
            .RunCycleAsync(Arg.Any<long>(), Arg.Any<DateTimeOffset>(), Arg.Any<CancellationToken>());
    }
}

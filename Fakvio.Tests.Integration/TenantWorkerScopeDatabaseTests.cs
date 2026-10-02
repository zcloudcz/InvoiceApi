using System.Net.Sockets;
using Fakvio.Application.Service;
using Fakvio.Domain.Entities;
using Fakvio.Infrastructure.Data;
using Fakvio.Infrastructure.Service;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;
using NSubstitute;
using Shouldly;

namespace Fakvio.Tests.Integration;

/// <summary>
/// Regression test: a background worker cycle must read/write the TENANT schema.
///
/// Bug: TenantDbContextFactory.CreateContextForCompanyAsync returns a NEW context, so the
/// DI-scoped TenantDbContext that services resolve inside a worker scope kept Schema = null
/// and queried "public" (ReminderWorker / RecurringInvoiceWorker silently did nothing or failed).
/// Needs a real PostgreSQL (InMemory ignores schemas). Skipped when unreachable; override the
/// connection with FAKVIO_TEST_POSTGRES.
/// </summary>
[Collection(RealPostgreSqlCollection.Name)]
public class TenantWorkerScopeDatabaseTests : IAsyncLifetime
{
    private const long CompanyId = 482;
    private const string DefaultConnectionString =
        "Host=localhost;Port=5432;Database=fakvio;Username=fakvio;Password=fakvio_dev;Timeout=5";

    private readonly string _schemaName = $"test_workerscope_{Guid.NewGuid():N}"[..40];
    private readonly string _masterDb = Guid.NewGuid().ToString();
    private NpgsqlDataSourceFactory? _dataSourceFactory;
    private bool _available;

    public async Task InitializeAsync()
    {
        _dataSourceFactory = new NpgsqlDataSourceFactory(new DatabaseOptions
        {
            ConnectionString = Environment.GetEnvironmentVariable("FAKVIO_TEST_POSTGRES") ?? DefaultConnectionString,
            AuthMode = DatabaseAuthMode.Password
        });
        try
        {
            await using var c = await _dataSourceFactory.Root.OpenConnectionAsync();
            _available = true;
        }
        catch (Exception ex) when (ex is NpgsqlException or SocketException)
        {
            return;
        }

        await using var master = NewMaster();
        master.Client.Add(new Client
        {
            Id = CompanyId, CompanyName = "Worker scope s.r.o.", RegistrationNumber = "48200000",
            IsIssuer = true, IsActive = true, IsVatPayer = true
        });
        master.CompanySystemSettings.Add(new CompanySystemSettings
            { CompanyId = CompanyId, SchemaName = _schemaName, IsProvisioned = false, IsActive = false });
        master.VatRate.Add(new VatRate
        {
            Name = "Základní 21 %", Rate = 21m, ValidFrom = new DateTime(2024, 1, 1, 0, 0, 0, DateTimeKind.Utc),
            IsDefault = true, IsActive = true
        });
        master.Currency.Add(new Currency { Code = "CZK", Name = "Česká koruna", Symbol = "Kč", DecimalPlaces = 2, IsActive = true });
        await master.SaveChangesAsync();

        await using var provisionMaster = NewMaster();
        (await new TenantProvisioningService(provisionMaster, _dataSourceFactory, _dataSourceFactory.Root,
            NullLogger<TenantProvisioningService>.Instance).ProvisionTenantAsync(CompanyId)).ShouldBeTrue();
    }

    public async Task DisposeAsync()
    {
        if (_available)
        {
            _dataSourceFactory!.Evict(_schemaName);
            await using var cmd = _dataSourceFactory.Root.CreateCommand($"DROP SCHEMA IF EXISTS \"{_schemaName}\" CASCADE");
            await cmd.ExecuteNonQueryAsync();
        }
        if (_dataSourceFactory is not null) await _dataSourceFactory.DisposeAsync();
    }

    private MasterDbContext NewMaster()
        => new(new DbContextOptionsBuilder<MasterDbContext>().UseInMemoryDatabase(_masterDb).Options);

    /// <summary>Same wiring as production: scoped MasterDbContext + TenantDbContext + the real factory.</summary>
    private ServiceProvider BuildProvider(Func<IServiceProvider, IReminderService> reminderService)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddDbContext<MasterDbContext>(o => o.UseInMemoryDatabase(_masterDb));
        services.AddDbContext<TenantDbContext>(o => o
            .UseNpgsql(_dataSourceFactory!.Root)
            .ReplaceService<IModelCacheKeyFactory, TenantModelCacheKeyFactory>());
        services.AddSingleton(_dataSourceFactory!.Root);
        services.AddSingleton<IConfiguration>(new ConfigurationBuilder().Build());
        services.AddSingleton(Substitute.For<ITenantResolver>());
        services.AddScoped<ITenantProvisioningService>(sp => new TenantProvisioningService(
            sp.GetRequiredService<MasterDbContext>(), _dataSourceFactory!, _dataSourceFactory!.Root,
            NullLogger<TenantProvisioningService>.Instance));
        services.AddScoped<ITenantDbContextFactory, TenantDbContextFactory>();
        services.AddScoped(reminderService);
        return services.BuildServiceProvider();
    }

    [SkippableFact]
    public async Task ReminderWorkerCycle_ReadsTenantSchema()
    {
        Skip.IfNot(_available, "PostgreSQL is not reachable — start it with 'docker compose up -d'.");

        // Probe service: resolved from the WORKER's scope and querying that scope's TenantDbContext,
        // exactly what IReminderService does in production.
        var seenIssuers = -1;
        using var provider = BuildProvider(sp =>
        {
            var probe = Substitute.For<IReminderService>();
            probe.ProcessOverdueInvoicesAsync(Arg.Any<CancellationToken>()).Returns(async _ =>
            {
                seenIssuers = await sp.GetRequiredService<TenantDbContext>().Client.CountAsync(c => c.IsIssuer);
                return 0;
            });
            return probe;
        });

        await new ReminderWorker(provider.GetRequiredService<IServiceScopeFactory>(),
            NullLogger<ReminderWorker>.Instance).RunOnceAsync(CancellationToken.None);

        seenIssuers.ShouldBe(1, "the worker scope must query the tenant schema, not 'public'");
    }
}

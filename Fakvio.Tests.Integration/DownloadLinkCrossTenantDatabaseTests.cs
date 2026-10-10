using System.Net;
using System.Net.Sockets;
using Fakvio.Application.Service;
using Fakvio.Contracts.Dto.DownloadLink;
using Fakvio.Domain.Entities;
using Fakvio.Domain.Enums;
using Fakvio.Infrastructure.Data;
using Fakvio.Infrastructure.Service;
using Fakvio.Tests.Integration.Fixtures;
using Microsoft.AspNetCore.Hosting;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;
using Shouldly;

namespace Fakvio.Tests.Integration;

/// <summary>
/// The security boundary of signed download links: a link minted for company A must never return a
/// record that exists only in company B.
///
/// Needs a REAL PostgreSQL: tenants are PostgreSQL schemas, and the InMemory provider used by
/// <see cref="FakvioFactory"/> ignores schemas (all tenants share one database there, so this cannot be
/// expressed). The whole API host runs here, only the tenant DbContext points at PostgreSQL; the master
/// database stays InMemory. Skipped when PostgreSQL is unreachable; override the connection with
/// FAKVIO_TEST_POSTGRES.
/// </summary>
[Collection(RealPostgreSqlCollection.Name)]
public class DownloadLinkCrossTenantDatabaseTests : IAsyncLifetime
{
    private const long CompanyA = 5241;
    private const long CompanyB = 5242;
    private const string DefaultConnectionString =
        "Host=localhost;Port=5432;Database=fakvio;Username=fakvio;Password=fakvio_dev;Timeout=5";

    private static string ConnectionString
        => Environment.GetEnvironmentVariable("FAKVIO_TEST_POSTGRES") ?? DefaultConnectionString;

    private readonly string _schemaA = $"test_dllink_a_{Guid.NewGuid():N}"[..40];
    private readonly string _schemaB = $"test_dllink_b_{Guid.NewGuid():N}"[..40];
    private RealTenantDbFactory? _factory;
    private long _invoiceIdInB;
    private bool _available;

    /// <summary>The normal test host, except that TenantDbContext is the production one (PostgreSQL, schema per tenant).</summary>
    private sealed class RealTenantDbFactory : FakvioFactory
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            base.ConfigureWebHost(builder);
            builder.UseSetting("ConnectionStrings:DefaultConnection", ConnectionString);
            builder.ConfigureServices(services =>
            {
                RemoveDbContextRegistrations<TenantDbContext>(services);
                services.AddDbContext<TenantDbContext>((sp, options) => options
                    .UseNpgsql(sp.GetRequiredService<NpgsqlDataSource>())
                    .ReplaceService<IModelCacheKeyFactory, TenantModelCacheKeyFactory>()
                    .ConfigureWarnings(w => w.Ignore(RelationalEventId.PendingModelChangesWarning)));

                // No background workers: with a real tenant database they would start working on the
                // two test tenants (and hold extra connections) while the test runs.
                services.RemoveAll<IHostedService>();
            });
        }
    }

    public async Task InitializeAsync()
    {
        // Build the host BEFORE the first Npgsql call: Program.cs switches Npgsql to its legacy timestamp
        // behaviour, and Npgsql reads that switch only once, the first time it is used in the process.
        _factory = new RealTenantDbFactory();
        using var scope = _factory.Services.CreateScope();
        try
        {
            await using var probe = await scope.ServiceProvider.GetRequiredService<NpgsqlDataSource>().OpenConnectionAsync();
            _available = true;
        }
        catch (Exception ex) when (ex is NpgsqlException or SocketException)
        {
            return;
        }

        try
        {
            await SeedTwoTenantsAsync(scope.ServiceProvider);
        }
        catch
        {
            // xUnit does not call DisposeAsync when InitializeAsync throws - do not leave schemas behind.
            await DisposeAsync();
            throw;
        }
    }

    private async Task SeedTwoTenantsAsync(IServiceProvider services)
    {
        var master = services.GetRequiredService<MasterDbContext>();

        // Master seed (SysAdmin user 1, currencies, VAT rates). NOT FakvioFactory.InitializeDatabase():
        // that also calls EnsureCreated() on the tenant context, which here would create tables in the
        // "public" schema of the real database. Same "half-initialized" guard as over there.
        master.Database.EnsureCreated();
        if (!master.User.Any())
        {
            master.Database.EnsureDeleted();
            master.Database.EnsureCreated();
        }

        foreach (var (id, schema) in new[] { (CompanyA, _schemaA), (CompanyB, _schemaB) })
        {
            master.Client.Add(new Client
            {
                Id = id, CompanyName = $"Tenant {id} s.r.o.", RegistrationNumber = $"{id}0000",
                IsIssuer = true, IsActive = true, IsVatPayer = true
            });
            master.CompanySystemSettings.Add(new CompanySystemSettings
                { CompanyId = id, SchemaName = schema, IsProvisioned = false, IsActive = false });
        }
        await master.SaveChangesAsync();

        // The host carries a test double for provisioning (it only flips flags); the real schemas are
        // created by the production service, exactly like TenantWorkerScopeDatabaseTests does.
        var provisioning = new TenantProvisioningService(
            master,
            services.GetRequiredService<INpgsqlDataSourceFactory>(),
            services.GetRequiredService<NpgsqlDataSource>(),
            NullLogger<TenantProvisioningService>.Instance);
        (await provisioning.ProvisionTenantAsync(CompanyA)).ShouldBeTrue();
        (await provisioning.ProvisionTenantAsync(CompanyB)).ShouldBeTrue();

        // One invoice, in company B's schema only.
        var tenantFactory = services.GetRequiredService<ITenantDbContextFactory>();
        await using var tenantB = (TenantDbContext)await tenantFactory.CreateContextForCompanyAsync(CompanyB);
        var invoice = new Invoice
        {
            DocumentNumber = "ONLY-IN-B", VariableSymbol = "5242", DocumentType = EDocumentType.Invoice,
            Status = EInvoiceStatus.Completed,
            IssueDate = new DateTime(2026, 3, 10, 0, 0, 0, DateTimeKind.Utc),
            DueDate = new DateTime(2026, 3, 24, 0, 0, 0, DateTimeKind.Utc),
            Issuer = await tenantB.Client.FirstAsync(c => c.IsIssuer),
            Client = new Client { CompanyName = "Customer a.s.", RegistrationNumber = "27082440", IsActive = true },
            Currency = await tenantB.Currency.FirstAsync(c => c.Code == "CZK"),
            TotalBeforeVat = 100m, TotalVat = 21m, TotalWithVat = 121m,
            InvoiceItem =
            [
                new InvoiceItem
                {
                    OrderIndex = 1, Description = "Consulting", Quantity = 1, Unit = "ks", UnitPrice = 100m,
                    VatRatePercentage = 21m, TotalBeforeVat = 100m, VatAmount = 21m, TotalWithVat = 121m
                }
            ]
        };
        tenantB.Invoice.Add(invoice);
        await tenantB.SaveChangesAsync();
        _invoiceIdInB = invoice.Id;
    }

    public async Task DisposeAsync()
    {
        if (_factory is null) return;
        // Resolving the factory makes the container its owner, so disposing the host below also closes
        // the pooled connections - this local PostgreSQL has a small connection limit.
        var dataSources = _factory.Services.GetRequiredService<INpgsqlDataSourceFactory>();
        if (!_available)
        {
            await _factory.DisposeAsync();
            return;
        }

        var root = _factory.Services.GetRequiredService<NpgsqlDataSource>();
        foreach (var schema in new[] { _schemaA, _schemaB })
        {
            dataSources.Evict(schema);
            await using var cmd = root.CreateCommand($"DROP SCHEMA IF EXISTS \"{schema}\" CASCADE");
            await cmd.ExecuteNonQueryAsync();
        }
        await _factory.DisposeAsync();
    }

    /// <summary>A correctly signed link (user 1 = the seeded SysAdmin) for the given company and invoice id.</summary>
    private string TokenFor(long companyId)
        => _factory!.Services.GetRequiredService<IDownloadLinkProtector>().Protect(
            new DownloadTokenPayload(companyId, 1, new CreateDownloadLinkDto { Kind = EDownloadKind.InvoiceIsdoc, Id = _invoiceIdInB }),
            out _);

    [SkippableFact]
    public async Task Download_TokenOfCompanyA_WithIdThatExistsOnlyInCompanyB_Returns404()
    {
        Skip.IfNot(_available, "PostgreSQL is not reachable — start it with 'docker compose up -d'.");
        var client = _factory!.CreateClient();

        // Control: the record is really there and downloadable with company B's own link...
        var own = await client.GetAsync($"/api/download/{TokenFor(CompanyB)}");
        own.StatusCode.ShouldBe(HttpStatusCode.OK);
        (await own.Content.ReadAsStringAsync()).ShouldContain("ONLY-IN-B");

        // ...and the very same id through company A's link does not exist.
        var foreign = await client.GetAsync($"/api/download/{TokenFor(CompanyA)}");

        foreign.StatusCode.ShouldBe(HttpStatusCode.NotFound);
        (await foreign.Content.ReadAsStringAsync()).ShouldNotContain("ONLY-IN-B");
    }
}

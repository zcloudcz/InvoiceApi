using System.Net.Sockets;
using System.Reflection;
using Fakvio.Domain.Entities;
using Fakvio.Domain.Enums;
using Fakvio.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Storage;
using Npgsql;
using Shouldly;

namespace Fakvio.Tests.Integration;

/// <summary>
/// Integration tests for Step 6 of tenant provisioning
/// (<c>TenantProvisioningService.CreateIssuerInTenantAsync</c>) run against a REAL
/// PostgreSQL schema instead of an InMemory store.
///
/// Why the real database matters here (issue #153): the unit tests in
/// <c>Fakvio.Tests.Unit/CreateIssuerInTenantTests</c> run on InMemory, which has no
/// foreign keys and no unique indexes. They can show that the copy produces the right
/// object graph, but they cannot show that the graph is actually *insertable*. The two
/// decisions this PR makes — copy the bank accounts and billing settings, but null out
/// the master number-sequence Ids — are only provable against real constraints:
/// carrying those Ids over is not untidy, it is a foreign key violation.
///
/// Each test class instance creates its own throwaway schema and drops it afterwards,
/// so the tests never collide with the developer's dev data or with each other.
///
/// The tests are skipped (not failed) when PostgreSQL is unreachable — the local dev
/// database is started with <c>docker compose up -d</c>. Override the connection with
/// the <c>FAKVIO_TEST_POSTGRES</c> environment variable.
/// </summary>
// Shares one xUnit collection with the other real-schema test classes so they never issue
// tenant DDL against the same PostgreSQL concurrently — see RealPostgreSqlCollection.
[Collection(RealPostgreSqlCollection.Name)]
public class TenantIssuerProvisioningDatabaseTests : IAsyncLifetime
{
    /// <summary>Matches docker-compose.yml — the local dev PostgreSQL.</summary>
    private const string DefaultConnectionString =
        "Host=localhost;Port=5432;Database=fakvio;Username=fakvio;Password=fakvio_dev;Timeout=5";

    private const string ConnectionStringEnvVar = "FAKVIO_TEST_POSTGRES";

    private const string SkipReason =
        "PostgreSQL is not reachable — start it with 'docker compose up -d' " +
        "or point " + ConnectionStringEnvVar + " at another instance.";

    /// <summary>
    /// Primary key of a number sequence that exists in no schema. Provisioning copies
    /// billing settings from master, where such an Id is perfectly legal (the master
    /// model declares no FK on it) — the tenant model does declare one.
    /// </summary>
    private const long DanglingNumberSequenceId = 999_999L;

    private readonly string _schemaName = $"test_issue153_{Guid.NewGuid():N}"[..40];
    private readonly string _connectionString =
        Environment.GetEnvironmentVariable(ConnectionStringEnvVar) ?? DefaultConnectionString;

    private NpgsqlDataSource? _dataSource;
    private bool _databaseAvailable;

    // ─── Fixture lifetime ─────────────────────────────────────────────────────

    public async Task InitializeAsync()
    {
        _dataSource = new NpgsqlDataSourceBuilder(_connectionString).Build();
        _databaseAvailable = await CanReachPostgreSqlAsync();

        if (!_databaseAvailable)
            return; // Every test skips with SkipReason.

        // Deliberately NOT wrapped in a try/catch: the reachability probe above is the only
        // reason this class is allowed to skip. Once the server answers, a failure to build
        // the schema is a real defect and must fail loudly instead of turning into a
        // silently-green skip.
        //
        // CreateTablesAsync — not EnsureCreatedAsync — because the "fakvio" database already
        // exists, and EnsureCreated is a no-op the moment the DATABASE is there; it never
        // looks at schemas. CreateTables emits CREATE SCHEMA + CREATE TABLE for the whole
        // tenant model (HasDefaultSchema pins every table to our throwaway schema) plus the
        // HasData seed rows, i.e. the same starting point a freshly provisioned tenant has.
        await using var context = CreateTenantContext();
        await context.Database.GetService<IRelationalDatabaseCreator>().CreateTablesAsync();
    }

    /// <summary>
    /// Opens and immediately closes one raw connection. EF wraps connection failures in a
    /// generic "transient failure" InvalidOperationException, which is indistinguishable
    /// from a genuine bug — so the reachability question is answered here, at the driver
    /// level, where the exception types are unambiguous.
    /// </summary>
    private async Task<bool> CanReachPostgreSqlAsync()
    {
        try
        {
            await using var connection = await _dataSource!.OpenConnectionAsync();
            return true;
        }
        catch (Exception ex) when (ex is NpgsqlException or SocketException)
        {
            return false;
        }
    }

    public async Task DisposeAsync()
    {
        if (_databaseAvailable && _dataSource is not null)
        {
            await using var command = _dataSource.CreateCommand(
                $"DROP SCHEMA IF EXISTS \"{_schemaName}\" CASCADE");
            await command.ExecuteNonQueryAsync();
        }

        if (_dataSource is not null)
            await _dataSource.DisposeAsync();
    }

    /// <summary>
    /// Builds a TenantDbContext bound to this test's throwaway schema, mirroring how
    /// TenantProvisioningService.CreateTenantContext builds the one it provisions with.
    /// A fresh context per call means assertions read from the database, not from a
    /// change tracker that still holds the objects the copy just created.
    /// </summary>
    private TenantDbContext CreateTenantContext()
    {
        var options = new DbContextOptionsBuilder<TenantDbContext>()
            .UseNpgsql(_dataSource!)
            // The tenant model is schema-dependent, so the model cache must be keyed by
            // schema — otherwise a context for another schema would reuse this model.
            .ReplaceService<IModelCacheKeyFactory, TenantModelCacheKeyFactory>()
            .Options;

        return new TenantDbContext(options) { Schema = _schemaName };
    }

    // ─── Arrange helpers ──────────────────────────────────────────────────────

    /// <summary>
    /// The master issuer as Step 2 of provisioning loads it: with bank accounts, billing
    /// settings, and number-sequence Ids that only mean something in the master schema.
    /// </summary>
    private static Client BuildMasterIssuer() => new()
    {
        Id = 42,
        CompanyName = "Test Company s.r.o.",
        RegistrationNumber = "12345678",
        TaxNumber = "CZ12345678",
        IsVatPayer = true,
        IsIssuer = true,
        IsActive = true,
        BankAccount =
        [
            new BankAccount
            {
                Id = 1,
                ClientId = 42,
                Label = "CZK účet",
                BankName = "Fio banka",
                AccountNumber = "2400123456/2010",
                IBAN = "CZ6520100000002400123456",
                SWIFT = "FIOBCZPP",
                CurrencyCode = "CZK",
                IsDefault = true
            }
        ],
        BillingSettings = new BillingSettings
        {
            Id = 7,
            ClientId = 42,
            DueDateCalculationType = EDueDateCalculationType.DaysFromEndOfMonth,
            DueDays = 21,
            DefaultPaymentMethod = EPaymentMethod.BankTransfer,
            CustomInvoiceNumberSequenceId = DanglingNumberSequenceId,
            CustomCreditNoteNumberSequenceId = DanglingNumberSequenceId
        }
    };

    /// <summary>
    /// Calls the private static <c>CreateIssuerInTenantAsync</c> — the production Step 6 —
    /// through reflection, the same way the unit tests reach it.
    /// </summary>
    private static async Task CopyIssuerIntoTenantAsync(TenantDbContext tenantContext, Client masterIssuer)
    {
        var method = typeof(Fakvio.Infrastructure.Service.TenantProvisioningService)
            .GetMethod("CreateIssuerInTenantAsync", BindingFlags.NonPublic | BindingFlags.Static)
            ?? throw new InvalidOperationException(
                "CreateIssuerInTenantAsync not found via reflection. Check the method name hasn't been renamed.");

        await (Task)method.Invoke(null, [tenantContext, masterIssuer, CancellationToken.None])!;
    }

    // ─── Tests ────────────────────────────────────────────────────────────────

    /// <summary>
    /// The whole point of issue #153: after provisioning, the tenant issuer must be able
    /// to issue an invoice — which needs a bank account and billing settings that survived
    /// a real INSERT, not just an in-memory object graph.
    /// </summary>
    [SkippableFact]
    public async Task CreateIssuerInTenant_PersistsBankAccountsAndBillingSettings()
    {
        Skip.IfNot(_databaseAvailable, SkipReason);

        await using (var writeContext = CreateTenantContext())
        {
            await CopyIssuerIntoTenantAsync(writeContext, BuildMasterIssuer());
        }

        await using var readContext = CreateTenantContext();
        var issuer = await readContext.Client
            .Include(c => c.BankAccount)
            .Include(c => c.BillingSettings)
            .SingleAsync(c => c.IsIssuer);

        var account = issuer.BankAccount.ShouldHaveSingleItem();
        account.AccountNumber.ShouldBe("2400123456/2010");
        account.IBAN.ShouldBe("CZ6520100000002400123456");
        account.IsDefault.ShouldBeTrue();
        account.ClientId.ShouldBe(issuer.Id);

        var billing = issuer.BillingSettings.ShouldNotBeNull();
        billing.DueDays.ShouldBe(21);
        billing.DueDateCalculationType.ShouldBe(EDueDateCalculationType.DaysFromEndOfMonth);
        billing.DefaultPaymentMethod.ShouldBe(EPaymentMethod.BankTransfer);
        billing.ClientId.ShouldBe(issuer.Id);
    }

    /// <summary>
    /// Half of the "do not copy the master number-sequence Ids" decision: the copy must
    /// leave them null.
    /// </summary>
    [SkippableFact]
    public async Task CreateIssuerInTenant_LeavesCustomNumberSequenceIdsNull()
    {
        Skip.IfNot(_databaseAvailable, SkipReason);

        await using (var writeContext = CreateTenantContext())
        {
            await CopyIssuerIntoTenantAsync(writeContext, BuildMasterIssuer());
        }

        await using var readContext = CreateTenantContext();
        var billing = await readContext.BillingSettings.SingleAsync();

        billing.CustomInvoiceNumberSequenceId.ShouldBeNull();
        billing.CustomCreditNoteNumberSequenceId.ShouldBeNull();
    }

    /// <summary>
    /// The other half, and the reason the first one is a requirement rather than a
    /// preference: the tenant schema really does constrain those columns, so a copied
    /// master Id would abort provisioning at Step 6 with a foreign key violation.
    /// (Steps 5 and 7 create the tenant's own sequences, and Step 7 runs after Step 6 —
    /// so there is no moment during provisioning at which a master Id would be valid.)
    /// </summary>
    [SkippableFact]
    public async Task TenantBillingSettings_WithMasterNumberSequenceId_ViolatesForeignKey()
    {
        Skip.IfNot(_databaseAvailable, SkipReason);

        await using var context = CreateTenantContext();
        context.Client.Add(new Client
        {
            CompanyName = "FK probe s.r.o.",
            RegistrationNumber = "87654321",
            IsIssuer = true,
            IsActive = true,
            BillingSettings = new BillingSettings
            {
                CustomInvoiceNumberSequenceId = DanglingNumberSequenceId
            }
        });

        var act = () => context.SaveChangesAsync();

        var exception = await Should.ThrowAsync<DbUpdateException>(act);
        exception.InnerException.ShouldBeOfType<PostgresException>()
            .SqlState.ShouldBe(PostgresErrorCodes.ForeignKeyViolation);
    }

    /// <summary>
    /// Provisioning is re-runnable, and the tenant schema enforces one BillingSettings row
    /// per client with a unique index. InMemory cannot fail that way, so the idempotence
    /// guarantee is only actually tested here.
    /// </summary>
    [SkippableFact]
    public async Task CreateIssuerInTenant_RunTwice_DoesNotDuplicateOrViolateUniqueIndex()
    {
        Skip.IfNot(_databaseAvailable, SkipReason);

        await using (var firstRun = CreateTenantContext())
        {
            await CopyIssuerIntoTenantAsync(firstRun, BuildMasterIssuer());
        }

        await using (var secondRun = CreateTenantContext())
        {
            await CopyIssuerIntoTenantAsync(secondRun, BuildMasterIssuer());
        }

        await using var readContext = CreateTenantContext();
        (await readContext.Client.CountAsync(c => c.IsIssuer)).ShouldBe(1);
        (await readContext.BankAccount.CountAsync()).ShouldBe(1);
        (await readContext.BillingSettings.CountAsync()).ShouldBe(1);
    }
}

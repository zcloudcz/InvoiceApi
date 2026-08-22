using System.Net.Sockets;
using Fakvio.Application.Service;
using Fakvio.Contracts.Dto.Invoice;
using Fakvio.Domain.Entities;
using Fakvio.Domain.Enums;
using Fakvio.Infrastructure.Data;
using Fakvio.Infrastructure.Service;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Extensions.Logging;
using Npgsql;
using NSubstitute;
using Shouldly;

namespace Fakvio.Tests.Integration;

/// <summary>
/// Issue #181 — <c>InvoiceService.CreateInvoiceAsync</c> against a REAL PostgreSQL schema,
/// with the REAL <c>NumberSequenceService</c> behind it.
///
/// Why the real database and the real sequence service matter here — the unit tests in
/// <c>Fakvio.Tests.Unit/InvoiceServiceCreateRollbackTests</c> mock <c>INumberSequenceService</c>,
/// so the mock never touches the database. In production that service is NOT inert: it shares
/// the very same <c>TenantDbContext</c> and calls <c>SaveChangesAsync</c> on it to persist the
/// incremented counter. Any invoice sitting in the change tracker at that moment is flushed as
/// a side effect of that save — which is exactly how the orphaned "DRAFT" row was born. A mocked
/// sequence service cannot exhibit that, so the guarantee "nothing is written until the end"
/// can only be proven here.
///
/// Each test class instance creates its own throwaway schema and drops it afterwards, so the
/// tests never collide with the developer's dev data or with each other.
///
/// The tests are skipped (not failed) when PostgreSQL is unreachable — the local dev database
/// is started with <c>docker compose up -d</c>. Override the connection with the
/// <c>FAKVIO_TEST_POSTGRES</c> environment variable.
/// </summary>
public class InvoiceCreateRollbackDatabaseTests : IAsyncLifetime
{
    /// <summary>Matches docker-compose.yml — the local dev PostgreSQL.</summary>
    private const string DefaultConnectionString =
        "Host=localhost;Port=5432;Database=fakvio;Username=fakvio;Password=fakvio_dev;Timeout=5";

    private const string ConnectionStringEnvVar = "FAKVIO_TEST_POSTGRES";

    private const string SkipReason =
        "PostgreSQL is not reachable — start it with 'docker compose up -d' " +
        "or point " + ConnectionStringEnvVar + " at another instance.";

    /// <summary>Id of the default Invoice sequence seeded by the tenant model (HasData).</summary>
    private const long DefaultInvoiceSequenceId = 1;

    private const long CustomerId = 1;
    private const long IssuerId = 2;
    private const long CzkCurrencyId = 1;   // seeded by the tenant model

    /// <summary>
    /// What the seeded default sequence produces on its first draw: prefix "INV" plus the
    /// "yyyyNNN" format for the issue date below.
    /// </summary>
    private const string ExpectedFirstNumber = "INV2026001";

    /// <summary>The digits of <see cref="ExpectedFirstNumber"/> — the auto-derived VariableSymbol.</summary>
    private const string DerivedVariableSymbol = "2026001";

    /// <summary>VariableSymbol an API caller types by hand; unrelated to any generated number.</summary>
    private const string ManualVariableSymbol = "5550001";

    private static readonly DateTime IssueDate = new(2026, 3, 1, 0, 0, 0, DateTimeKind.Utc);

    private readonly string _schemaName = $"test_issue181_{Guid.NewGuid():N}"[..40];
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
        // reason this class is allowed to skip. Once the server answers, a failure to build the
        // schema is a real defect and must fail loudly instead of turning into a silent skip.
        //
        // CreateTablesAsync — not EnsureCreatedAsync — because the "fakvio" database already
        // exists, and EnsureCreated is a no-op the moment the DATABASE is there; it never looks
        // at schemas. CreateTables emits CREATE SCHEMA + CREATE TABLE for the whole tenant model
        // plus its HasData seed rows, so the schema starts out like a freshly provisioned tenant:
        // currencies, VAT rates and the four default number sequences are already there.
        await using var context = CreateTenantContext();
        await context.Database.GetService<IRelationalDatabaseCreator>().CreateTablesAsync();

        await SeedClientsAsync();
    }

    /// <summary>
    /// Opens and immediately closes one raw connection. EF wraps connection failures in a
    /// generic "transient failure" InvalidOperationException, which is indistinguishable from a
    /// genuine bug — so the reachability question is answered here, at the driver level, where
    /// the exception types are unambiguous.
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

    // ─── Arrange helpers ──────────────────────────────────────────────────────

    /// <summary>
    /// Builds a TenantDbContext bound to this test's throwaway schema. A fresh context per call
    /// means assertions read from the database, not from a change tracker that still holds the
    /// objects the service just worked with — and it also mirrors one HTTP request.
    /// </summary>
    private TenantDbContext CreateTenantContext()
    {
        var options = new DbContextOptionsBuilder<TenantDbContext>()
            .UseNpgsql(_dataSource!)
            // The tenant model is schema-dependent, so the model cache must be keyed by schema —
            // otherwise a context for another schema would reuse this model.
            .ReplaceService<IModelCacheKeyFactory, TenantModelCacheKeyFactory>()
            .Options;

        return new TenantDbContext(options) { Schema = _schemaName };
    }

    /// <summary>
    /// Wires the production object graph for one "request": InvoiceService on top of the REAL
    /// NumberSequenceService, both sharing a single context — the sharing is the point (see the
    /// class summary). The master context and tenant resolver are only in the sequence service's
    /// constructor for DI compatibility; it never uses them.
    /// </summary>
    private (InvoiceService Service, TenantDbContext Context) CreateRequestScope()
    {
        var context = CreateTenantContext();

        var numberSequence = new NumberSequenceService(
            context,
            masterContext: null!,
            tenantResolver: Substitute.For<ITenantResolver>(),
            Substitute.For<ILogger<NumberSequenceService>>());

        var service = new InvoiceService(context, numberSequence, Substitute.For<ILogger<InvoiceService>>());

        return (service, context);
    }

    /// <summary>Seeds the customer and the issuer. Currencies and sequences come from HasData.</summary>
    private async Task SeedClientsAsync()
    {
        await using var context = CreateTenantContext();

        context.Client.Add(new Client
        {
            Id = CustomerId,
            CompanyName = "Customer A s.r.o.",
            RegistrationNumber = "11111111",
            IsIssuer = false,
            IsActive = true
        });

        context.Client.Add(new Client
        {
            Id = IssuerId,
            CompanyName = "My Company s.r.o.",
            RegistrationNumber = "22222222",
            IsIssuer = true,
            IsActive = true,
            IsVatPayer = false
        });

        await context.SaveChangesAsync();
    }

    /// <summary>Activates or deactivates the seeded default Invoice sequence.</summary>
    private async Task SetDefaultInvoiceSequenceActiveAsync(bool isActive)
    {
        await using var context = CreateTenantContext();

        var sequence = await context.NumberSequence.SingleAsync(s => s.Id == DefaultInvoiceSequenceId);
        sequence.IsActive = isActive;

        await context.SaveChangesAsync();
    }

    /// <summary>Inserts an invoice that already occupies a VariableSymbol.</summary>
    private async Task SeedExistingInvoiceAsync(string variableSymbol, string documentNumber)
    {
        await using var context = CreateTenantContext();

        context.Invoice.Add(new Invoice
        {
            DocumentNumber = documentNumber,
            DocumentType = EDocumentType.Invoice,
            Status = EInvoiceStatus.Completed,
            ClientId = CustomerId,
            IssuerId = IssuerId,
            CurrencyId = CzkCurrencyId,
            VariableSymbol = variableSymbol,
            IssueDate = IssueDate,
            TaxableSupplyDate = IssueDate,
            TotalBeforeVat = 1000m,
            TotalVat = 0m,
            TotalWithVat = 1000m
        });

        await context.SaveChangesAsync();
    }

    private static CreateInvoiceDto MakeInvoiceDto(string? manualVariableSymbol = null) => new()
    {
        DocumentType = EDocumentType.Invoice,
        ClientId = CustomerId,
        IssuerId = IssuerId,
        CurrencyId = CzkCurrencyId,
        IssueDate = IssueDate,
        TaxableSupplyDate = IssueDate,
        VariableSymbol = manualVariableSymbol,
        VariableSymbolIsManualOverride = manualVariableSymbol is not null,
        InvoiceItem =
        [
            new CreateInvoiceItemDto
            {
                OrderIndex = 1,
                Description = "Service",
                Quantity = 1,
                Unit = "pcs",
                UnitPrice = 1000
            }
        ]
    };

    /// <summary>Reads the whole Invoice table through a context nobody else has touched.</summary>
    private async Task<List<Invoice>> LoadAllInvoicesAsync()
    {
        await using var context = CreateTenantContext();
        return await context.Invoice.AsNoTracking().OrderBy(i => i.Id).ToListAsync();
    }

    private async Task<long> LoadSequenceCounterAsync()
    {
        await using var context = CreateTenantContext();
        return (await context.NumberSequence.AsNoTracking()
            .SingleAsync(s => s.Id == DefaultInvoiceSequenceId)).CurrentNumber;
    }

    // ─── Tests ────────────────────────────────────────────────────────────────

    /// <summary>
    /// AC 1 against real DDL: the series is deactivated, numbering fails, and the table must
    /// stay empty. Before the fix a row with DocumentNumber = "DRAFT" was committed here — and
    /// the unique index did not object, because its filter excludes exactly that value.
    /// </summary>
    [SkippableFact]
    public async Task FailedNumberGeneration_LeavesNoInvoiceRowInTheDatabase()
    {
        Skip.IfNot(_databaseAvailable, SkipReason);

        // Arrange — no active default sequence for Invoice
        await SetDefaultInvoiceSequenceActiveAsync(false);

        // Act
        var (service, context) = CreateRequestScope();
        await using (context)
        {
            var ex = await Should.ThrowAsync<InvalidOperationException>(
                () => service.CreateInvoiceAsync(MakeInvoiceDto(ManualVariableSymbol)));
            ex.Message.ShouldContain("number sequence");
        }

        // Assert
        (await LoadAllInvoicesAsync()).ShouldBeEmpty();
    }

    /// <summary>
    /// AC 2 end-to-end — the defect of issue #181. Attempt 1 fails on numbering; the admin
    /// activates the series; the user repeats the action with the same hand-typed VariableSymbol,
    /// as the error message from issue #155 instructs. Before the fix the retry was rejected with
    /// "An invoice with Variable Symbol '5550001' already exists" — the phantom draft from
    /// attempt 1 still held the VS.
    /// </summary>
    [SkippableFact]
    public async Task FailedNumberGeneration_ThenRetryWithTheSameManualVs_Succeeds()
    {
        Skip.IfNot(_databaseAvailable, SkipReason);

        // Arrange — attempt 1 runs against a deactivated series
        await SetDefaultInvoiceSequenceActiveAsync(false);

        var (firstService, firstContext) = CreateRequestScope();
        await using (firstContext)
        {
            await Should.ThrowAsync<InvalidOperationException>(
                () => firstService.CreateInvoiceAsync(MakeInvoiceDto(ManualVariableSymbol)));
        }

        // Arrange — the admin fixes the configuration
        await SetDefaultInvoiceSequenceActiveAsync(true);

        // Act — attempt 2 is a separate request, i.e. a separate context
        var (retryService, retryContext) = CreateRequestScope();
        InvoiceDto created;
        await using (retryContext)
        {
            created = await retryService.CreateInvoiceAsync(MakeInvoiceDto(ManualVariableSymbol));
        }

        // Assert — the retry went through and kept the user's own VariableSymbol
        created.DocumentNumber.ShouldBe(ExpectedFirstNumber);
        created.VariableSymbol.ShouldBe(ManualVariableSymbol);

        var stored = await LoadAllInvoicesAsync();
        stored.Count.ShouldBe(1, customMessage: "The failed attempt must not survive as a second row");
        stored[0].VariableSymbol.ShouldBe(ManualVariableSymbol);
    }

    /// <summary>
    /// The proof that only a real sequence service can give: numbering SUCCEEDS (so
    /// NumberSequenceService really does call SaveChangesAsync on the shared context) and the
    /// duplicate-VS guard throws immediately afterwards. The counter increment must be committed
    /// — that is what shows the shared save happened — while the invoice must not be, which is
    /// only true because the entity is still outside the change tracker at that moment.
    /// </summary>
    [SkippableFact]
    public async Task DuplicateVariableSymbolAfterNumberWasDrawn_LeavesNoInvoiceRowBehind()
    {
        Skip.IfNot(_databaseAvailable, SkipReason);

        // Arrange — an existing invoice already owns the VS that will be derived from INV2026001
        await SeedExistingInvoiceAsync(DerivedVariableSymbol, documentNumber: "INV2025999");

        // Act — the number is drawn first, the collision is only found afterwards
        var (service, context) = CreateRequestScope();
        await using (context)
        {
            var ex = await Should.ThrowAsync<InvalidOperationException>(
                () => service.CreateInvoiceAsync(MakeInvoiceDto()));
            ex.Message.ShouldContain("Variable Symbol");
        }

        // Assert — the sequence counter moved, so the shared SaveChangesAsync really ran…
        (await LoadSequenceCounterAsync()).ShouldBe(1,
            customMessage: "The number was drawn, so the sequence service saved on the shared context");

        // …and yet that save did not drag the half-built invoice into the table with it.
        var stored = await LoadAllInvoicesAsync();
        stored.Count.ShouldBe(1, customMessage: "Only the pre-existing invoice may remain");
        stored[0].DocumentNumber.ShouldBe("INV2025999");
    }

    /// <summary>
    /// Happy path against real constraints: moving the single write to the end of the method must
    /// still produce a complete, insertable invoice — header, items and all.
    /// </summary>
    [SkippableFact]
    public async Task SuccessfulCreate_StoresTheInvoiceWithItsItems()
    {
        Skip.IfNot(_databaseAvailable, SkipReason);

        // Act
        var (service, context) = CreateRequestScope();
        InvoiceDto created;
        await using (context)
        {
            created = await service.CreateInvoiceAsync(MakeInvoiceDto());
        }

        // Assert
        created.DocumentNumber.ShouldBe(ExpectedFirstNumber);
        created.VariableSymbol.ShouldBe(DerivedVariableSymbol);

        await using var verify = CreateTenantContext();
        var stored = await verify.Invoice.AsNoTracking().Include(i => i.InvoiceItem).SingleAsync();
        stored.DocumentNumber.ShouldBe(ExpectedFirstNumber);
        stored.Status.ShouldBe(EInvoiceStatus.Draft);
        stored.InvoiceItem.Count.ShouldBe(1);
    }
}

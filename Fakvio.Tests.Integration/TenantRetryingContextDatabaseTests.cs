using System.Net.Sockets;
using AresService;
using Fakvio.Application.Service;
using Fakvio.Contracts.Dto.Client;
using Fakvio.Domain.Entities;
using Fakvio.Domain.Enums;
using Fakvio.Infrastructure.Data;
using Fakvio.Infrastructure.Service;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;
using NSubstitute;
using Shouldly;

namespace Fakvio.Tests.Integration;

/// <summary>
/// PR #444 review findings 1 and 2 — behavior that only shows up on a REAL PostgreSQL with
/// the production TenantDbContext options, never on the InMemory provider the unit tests use:
///
/// * <b>Recurring invoices</b>: production TenantDbContext uses <c>EnableRetryOnFailure</c>.
///   With a retrying execution strategy, EF Core throws on a plain user-initiated
///   <c>BeginTransactionAsync</c> unless it runs inside <c>CreateExecutionStrategy().ExecuteAsync</c>.
///   InMemory has no execution strategy, so the unit tests could not see that no recurring
///   invoice was ever generated.
/// * <b>CSV client import</b>: a failed INSERT (value longer than its column, unique IČO
///   violation 23505) used to stay tracked as "Added" and fail every following row.
///   InMemory has neither column lengths nor unique indexes, so the INSERT never fails there.
///
/// Each test class instance migrates its own throwaway tenant schema and drops it afterwards.
/// Skipped (not failed) when PostgreSQL is unreachable — start the local dev database with
/// <c>docker compose up -d</c>, or point <c>FAKVIO_TEST_POSTGRES</c> at another LOCAL instance.
/// </summary>
[Collection(RealPostgreSqlCollection.Name)]
public class TenantRetryingContextDatabaseTests : IAsyncLifetime
{
    /// <summary>Matches docker-compose.yml — the local dev PostgreSQL.</summary>
    private const string DefaultConnectionString =
        "Host=localhost;Port=5432;Database=fakvio;Username=fakvio;Password=fakvio_dev;Timeout=5";

    private const string ConnectionStringEnvVar = "FAKVIO_TEST_POSTGRES";

    private const string SkipReason =
        "PostgreSQL is not reachable — start it with 'docker compose up -d' " +
        "or point " + ConnectionStringEnvVar + " at another instance.";

    private readonly string _schemaName = $"test_pr444_{Guid.NewGuid():N}"[..40];
    private readonly string _connectionString =
        Environment.GetEnvironmentVariable(ConnectionStringEnvVar) ?? DefaultConnectionString;

    private NpgsqlDataSourceFactory? _dataSourceFactory;
    private bool _databaseAvailable;

    // ─── Fixture lifetime ─────────────────────────────────────────────────────

    public async Task InitializeAsync()
    {
        // Same reason as ApiKeyDatabaseConstraintTests: both production hosts set this switch at
        // startup, but in the test process it depends on which class ran first. Set it here.
        AppContext.SetSwitch("Npgsql.EnableLegacyTimestampBehavior", true);

        _dataSourceFactory = new NpgsqlDataSourceFactory(new DatabaseOptions
        {
            ConnectionString = _connectionString,
            AuthMode = DatabaseAuthMode.Password
        });

        _databaseAvailable = await CanReachPostgreSqlAsync();
        if (!_databaseAvailable)
            return;

        await using (var createSchema = _dataSourceFactory.Root.CreateCommand($"CREATE SCHEMA \"{_schemaName}\""))
        {
            await createSchema.ExecuteNonQueryAsync();
        }

        await using var context = CreateTenantContext();
        await context.Database.MigrateAsync();
    }

    private async Task<bool> CanReachPostgreSqlAsync()
    {
        try
        {
            await using var connection = await _dataSourceFactory!.Root.OpenConnectionAsync();
            return true;
        }
        catch (Exception ex) when (ex is NpgsqlException or SocketException)
        {
            return false;
        }
    }

    public async Task DisposeAsync()
    {
        if (_databaseAvailable && _dataSourceFactory is not null)
        {
            _dataSourceFactory.Evict(_schemaName);
            await using var command = _dataSourceFactory.Root.CreateCommand($"DROP SCHEMA IF EXISTS \"{_schemaName}\" CASCADE");
            await command.ExecuteNonQueryAsync();
        }

        if (_dataSourceFactory is not null)
            await _dataSourceFactory.DisposeAsync();
    }

    /// <summary>
    /// A TenantDbContext with the SAME retry setting production uses
    /// (TenantDbContextFactory.CreateTenantContext / ServiceCollectionExtensions) — that setting
    /// is what finding 1 is about, so leaving it out would make the test prove nothing.
    /// <c>includePublicInSearchPath: false</c> + a schema-local history table keep MigrateAsync
    /// from reading the dev database's own tables in <c>public</c>.
    /// </summary>
    private TenantDbContext CreateTenantContext(params IInterceptor[] interceptors)
    {
        var options = new DbContextOptionsBuilder<TenantDbContext>()
            .UseNpgsql(_dataSourceFactory!.GetForSchema(_schemaName, includePublicInSearchPath: false), b =>
            {
                b.MigrationsAssembly("Fakvio.Infrastructure");
                b.EnableRetryOnFailure(maxRetryCount: 3, maxRetryDelay: TimeSpan.FromSeconds(5), errorCodesToAdd: null);
                b.MigrationsHistoryTable("__EFMigrationsHistory", _schemaName);
            })
            .ReplaceService<IModelCacheKeyFactory, TenantModelCacheKeyFactory>()
            .ConfigureWarnings(w => w.Ignore(RelationalEventId.PendingModelChangesWarning))
            .AddInterceptors(interceptors)
            .Options;

        return new TenantDbContext(options) { Schema = _schemaName };
    }

    // ─── Finding 1: recurring invoice generation ──────────────────────────────

    [SkippableFact]
    public async Task RunCycleAsync_WithRetryingExecutionStrategy_GeneratesTheInvoiceAndAdvancesTheSchedule()
    {
        Skip.IfNot(_databaseAvailable, SkipReason);

        long scheduleId;
        await using (var arrange = CreateTenantContext())
        {
            var issuer = new Client { CompanyName = "Moje firma", RegistrationNumber = "44400001", IsIssuer = true, IsActive = true };
            var customer = new Client { CompanyName = "Odběratel", RegistrationNumber = "44400002", IsActive = true };
            arrange.Client.AddRange(issuer, customer);
            await arrange.SaveChangesAsync();

            var template = new InvoiceTemplate
            {
                Name = "Měsíční hosting",
                DocumentType = EDocumentType.Invoice,
                Status = EInvoiceStatus.Draft,
                DocumentNumber = "TEMPLATE",
                IssueDate = DateTime.UtcNow,
                DueDate = DateTime.UtcNow,
                TaxableSupplyDate = DateTime.UtcNow,
                IssuerId = issuer.Id,
                CurrencyId = (await arrange.Currency.FirstAsync()).Id,
                IsActive = true,
                DueDateOffsetDays = 14,
                InvoiceItem = [new InvoiceItem { OrderIndex = 1, Description = "Hosting", Quantity = 1, Unit = "měs", UnitPrice = 500 }]
            };
            arrange.Set<InvoiceTemplate>().Add(template);
            await arrange.SaveChangesAsync();

            var schedule = new RecurringInvoiceSchedule
            {
                TemplateId = template.Id,
                ClientId = customer.Id,
                Frequency = ERecurrenceFrequency.Monthly,
                IntervalCount = 1,
                DayOfMonth = 15,
                NextRunAt = new DateTimeOffset(2026, 1, 15, 8, 0, 0, TimeSpan.Zero),
                IsActive = true,
            };
            arrange.RecurringInvoiceSchedule.Add(schedule);
            await arrange.SaveChangesAsync();
            scheduleId = schedule.Id;
        }

        await using var context = CreateTenantContext();
        var notifications = Substitute.For<INotificationService>();
        var service = CreateRecurringService(context, notifications);

        var generated = await service.RunCycleAsync(companyId: 444, new DateTimeOffset(2026, 1, 16, 0, 0, 0, TimeSpan.Zero));

        // Before the fix: 0 generated, LastError = "…does not support user-initiated transactions…".
        await using var assert = CreateTenantContext();
        var stored = await assert.RecurringInvoiceSchedule.SingleAsync(s => s.Id == scheduleId);
        stored.LastError.ShouldBeNull();
        generated.ShouldBe(1);
        stored.OccurrenceCount.ShouldBe(1);
        stored.NextRunAt.ShouldBe(new DateTimeOffset(2026, 2, 15, 8, 0, 0, TimeSpan.Zero));
        (await assert.Invoice.CountAsync(i => i.DocumentNumber != "TEMPLATE")).ShouldBe(1);
        await notifications.DidNotReceiveWithAnyArgs().CreateForAllUsersAsync(default, default!, default!, default, default!, default, default);
    }

    private static RecurringInvoiceService CreateRecurringService(TenantDbContext context, INotificationService notifications)
    {
        var numberSequence = Substitute.For<INumberSequenceService>();
        var counter = 0;
        numberSequence
            .GenerateNextNumberForDocumentTypeAsync(
                Arg.Any<EDocumentType>(), Arg.Any<DateTime>(), Arg.Any<string?>(), Arg.Any<string?>(), Arg.Any<CancellationToken>())
            .Returns(_ => $"INV-444-{++counter}");

        var invoiceService = new InvoiceService(
            context, numberSequence, Substitute.For<ITenantReadinessService>(), NullLogger<InvoiceService>.Instance);
        var templateService = new InvoiceTemplateService(context, invoiceService, NullLogger<InvoiceTemplateService>.Instance);

        return new RecurringInvoiceService(
            context, templateService, notifications, Substitute.For<IEmailService>(), NullLogger<RecurringInvoiceService>.Instance);
    }

    // ─── Finding 2: CSV client import ─────────────────────────────────────────

    private static ClientCsvImportService CreateImportService(TenantDbContext context)
        => new(new ClientService(context, Substitute.For<IAresService>(), NullLogger<ClientService>.Instance),
               NullLogger<ClientCsvImportService>.Instance);

    private static CreateClientDto ImportRow(string ico, string postalCode = "11000") => new()
    {
        CompanyName = $"Firma {ico}",
        RegistrationNumber = ico,
        Address =
        [
            new CreateAddressDto
            {
                AddressType = EAddressType.Primary, Street = "Dlouhá 1", City = "Praha",
                PostalCode = postalCode, Country = "Czech Republic", IsPrimary = true
            }
        ]
    };

    [SkippableFact]
    public async Task ConfirmAsync_RowFailsInTheDatabase_FollowingRowsAreStillImported()
    {
        Skip.IfNot(_databaseAvailable, SkipReason);

        await using var context = CreateTenantContext();
        var service = CreateImportService(context);

        // Row 2's PSČ exceeds the varchar(20) column → the INSERT fails with a DbUpdateException.
        var result = await service.ConfirmAsync(new()
        {
            Clients = [ImportRow("44410001"), ImportRow("44410002", postalCode: new string('9', 25)), ImportRow("44410003")]
        });

        result.CreatedCount.ShouldBe(2); // before the fix: 1 — row 3 re-sent row 2's broken INSERT
        result.Errors.Count.ShouldBe(1);
        result.SkippedCount.ShouldBe(0);
    }

    [SkippableFact]
    public async Task ConfirmAsync_ConcurrentInsertOfTheSameIco_IsSkippedAndDoesNotBreakTheNextRow()
    {
        Skip.IfNot(_databaseAvailable, SkipReason);

        // Simulates another request inserting the same IČO in the window between
        // ClientService's "already exists" check and its own INSERT → unique violation 23505.
        var race = new RunBeforeFirstSave(async () =>
        {
            await using var otherRequest = CreateTenantContext();
            otherRequest.Client.Add(new Client { CompanyName = "Souběžný import", RegistrationNumber = "44420001", IsActive = true });
            await otherRequest.SaveChangesAsync();
        });
        await using var context = CreateTenantContext(race);
        var service = CreateImportService(context);

        var result = await service.ConfirmAsync(new() { Clients = [ImportRow("44420001"), ImportRow("44420002")] });

        result.SkippedCount.ShouldBe(1); // before the fix: reported as an error
        result.CreatedCount.ShouldBe(1);
        result.Errors.ShouldBeEmpty();
    }

    /// <summary>Runs an action once, right before the first SaveChanges on the context.</summary>
    private sealed class RunBeforeFirstSave(Func<Task> action) : SaveChangesInterceptor
    {
        private bool _done;

        public override async ValueTask<InterceptionResult<int>> SavingChangesAsync(
            DbContextEventData eventData, InterceptionResult<int> result, CancellationToken cancellationToken = default)
        {
            if (!_done)
            {
                _done = true;
                await action();
            }

            return result;
        }
    }
}

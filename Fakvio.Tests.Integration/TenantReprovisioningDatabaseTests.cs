using System.Net.Sockets;
using Fakvio.Application.Service;
using Fakvio.Contracts.Dto.User;
using Fakvio.Domain.Entities;
using Fakvio.Domain.Enums;
using Fakvio.Infrastructure.Data;
using Fakvio.Infrastructure.Service;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;
using NSubstitute;
using Shouldly;

namespace Fakvio.Tests.Integration;

/// <summary>
/// Serializes the test classes that build throwaway schemas in the real PostgreSQL.
/// xUnit runs each class in its own collection — i.e. in parallel — by default, and two
/// classes issuing tenant DDL (CREATE SCHEMA, CREATE TABLE, migrations) against the same
/// database at the same time contend on the system catalogs. That was observed as a whole
/// class failing at once, with errors unrelated to what it asserts.
/// </summary>
[CollectionDefinition(Name)]
public class RealPostgreSqlCollection
{
    public const string Name = "Real PostgreSQL";
}

/// <summary>
/// Integration tests for issue #192 — provisioning must never run a second time over a
/// tenant that is already established.
///
/// Why this needs a REAL PostgreSQL: the damage is done by raw SQL that InMemory cannot
/// express. Step 5 of provisioning (<c>CopyCodeTablesAsync</c>) issues
/// <c>DELETE FROM "tenant_x"."VatRate"</c> plus <c>ALTER SEQUENCE … RESTART WITH 1</c> and
/// then re-inserts the master rows. InMemory has neither foreign keys nor identity
/// sequences, so on InMemory that sequence of statements looks harmless. Against a real
/// schema it is either a foreign key violation (documents point at the deleted rows) or —
/// worse, because it is silent — a renumbering of the VAT rates and currencies underneath
/// invoices that were already issued.
///
/// The test therefore walks the production path end to end: provision once (a new company),
/// issue an invoice on the copied code tables, then provision again (what every invited
/// colleague used to trigger through <c>UserService.SetPasswordAsync</c>).
///
/// Each test class instance creates its own throwaway schema and drops it afterwards, so
/// the tests never collide with the developer's dev data or with each other.
///
/// The tests are skipped (not failed) when PostgreSQL is unreachable — the local dev
/// database is started with <c>docker compose up -d</c>. Override the connection with
/// the <c>FAKVIO_TEST_POSTGRES</c> environment variable.
/// </summary>
[Collection(RealPostgreSqlCollection.Name)]
public class TenantReprovisioningDatabaseTests : IAsyncLifetime
{
    /// <summary>Matches docker-compose.yml — the local dev PostgreSQL.</summary>
    private const string DefaultConnectionString =
        "Host=localhost;Port=5432;Database=fakvio;Username=fakvio;Password=fakvio_dev;Timeout=5";

    private const string ConnectionStringEnvVar = "FAKVIO_TEST_POSTGRES";

    private const string SkipReason =
        "PostgreSQL is not reachable — start it with 'docker compose up -d' " +
        "or point " + ConnectionStringEnvVar + " at another instance.";

    /// <summary>Master-DB id of the company being provisioned. Named after the issue.</summary>
    private const long CompanyId = 192L;

    /// <summary>The one document the tests issue — the reason a code table re-seed hurts.</summary>
    private const string InvoiceDocumentNumber = "INV-192-0001";

    private readonly string _schemaName = $"test_issue192_{Guid.NewGuid():N}"[..40];
    private readonly string _connectionString =
        Environment.GetEnvironmentVariable(ConnectionStringEnvVar) ?? DefaultConnectionString;

    /// <summary>
    /// The in-memory master database shared by every context this class opens. Contexts come
    /// and go per simulated request; the data behind them stays.
    /// </summary>
    private readonly string _masterDatabaseName = Guid.NewGuid().ToString();

    private NpgsqlDataSourceFactory? _dataSourceFactory;
    private bool _databaseAvailable;

    // ─── Fixture lifetime ─────────────────────────────────────────────────────

    public async Task InitializeAsync()
    {
        // The real factory, in password mode — the same object graph production builds,
        // so the per-schema search_path handling under test is the production one.
        _dataSourceFactory = new NpgsqlDataSourceFactory(new DatabaseOptions
        {
            ConnectionString = _connectionString,
            AuthMode = DatabaseAuthMode.Password
        });

        _databaseAvailable = await CanReachPostgreSqlAsync();

        if (!_databaseAvailable)
            return; // Every test skips with SkipReason.

        SeedMaster();
    }

    /// <summary>
    /// Opens a context over this class's in-memory master database.
    ///
    /// Every simulated request gets its OWN context and closes it again, exactly as the API
    /// does with its scoped DbContext. Sharing one context across the two provisioning runs
    /// would make the second run read the CompanySystemSettings row straight out of the first
    /// run's change tracker — the guard under test would then be looking at an in-memory
    /// object instead of at persisted state, and the test would prove less than it claims.
    ///
    /// The MASTER side stays InMemory on purpose: provisioning only reads company data and
    /// writes the IsProvisioned flag there, none of which needs real SQL. The tenant side —
    /// the destructive one — is the real database.
    /// </summary>
    private MasterDbContext CreateMasterContext()
        => new(new DbContextOptionsBuilder<MasterDbContext>()
            .UseInMemoryDatabase(_masterDatabaseName)
            .Options);

    /// <summary>
    /// Runs one provisioning call the way a request does: fresh master context, fresh service,
    /// both gone by the time the call returns. <paramref name="dataSourceFactory"/> lets a test
    /// pass a spy instead of the real factory.
    /// </summary>
    private async Task<bool> ProvisionAsync(INpgsqlDataSourceFactory? dataSourceFactory = null)
    {
        await using var masterContext = CreateMasterContext();

        var service = CreateProvisioningService(masterContext, dataSourceFactory ?? _dataSourceFactory!);

        return await service.ProvisionTenantAsync(CompanyId);
    }

    private static TenantProvisioningService CreateProvisioningService(
        MasterDbContext masterContext,
        INpgsqlDataSourceFactory dataSourceFactory)
        => new(
            masterContext,
            dataSourceFactory,
            dataSourceFactory.Root,
            NullLogger<TenantProvisioningService>.Instance);

    /// <summary>
    /// The real factory behind a substitute, so a test can both let provisioning work for
    /// real and assert what it asked the factory for.
    ///
    /// Asking for a per-schema data source is the first thing that happens once the guard
    /// lets a run through (step 4 builds the tenant context from it), and nothing before
    /// that touches the tenant schema. "GetForSchema was never called" is therefore the
    /// sharpest available statement of "the tenant schema was not opened at all" — sharper
    /// than looking at the data afterwards, which the foreign keys would protect anyway.
    /// </summary>
    private INpgsqlDataSourceFactory CreateFactorySpy()
    {
        var spy = Substitute.For<INpgsqlDataSourceFactory>();

        spy.Root.Returns(_dataSourceFactory!.Root);
        spy.GetForSchema(Arg.Any<string>(), Arg.Any<bool>())
            .Returns(callInfo => _dataSourceFactory!.GetForSchema(
                callInfo.ArgAt<string>(0), callInfo.ArgAt<bool>(1)));

        return spy;
    }

    /// <summary>
    /// A password hasher is irrelevant to what these tests assert, so it is stubbed — the
    /// real one is BCrypt with work factor 12 and would only make the suite slower.
    /// </summary>
    private static IAuthService CreateAuthServiceStub()
    {
        var authService = Substitute.For<IAuthService>();
        authService.HashPassword(Arg.Any<string>()).Returns(call => $"HASH:{call.Arg<string>()}");
        return authService;
    }

    /// <summary>
    /// Invites a colleague into the established company and returns the invitation token
    /// from the email link. Runs through the real service in a request of its own.
    /// </summary>
    private async Task<string> InviteColleagueAsync()
    {
        await using var masterContext = CreateMasterContext();

        var userService = new UserService(
            masterContext,
            CreateAuthServiceStub(),
            CreateProvisioningService(masterContext, _dataSourceFactory!),
            NullLogger<UserService>.Instance);

        var invited = await userService.InviteUserAsync(new InviteUserDto
        {
            Email = "kolega@zavedena-firma.cz",
            FirstName = "Kolega",
            LastName = "Pozvaný",
            Role = EUserRole.Admin,
            CompanyId = CompanyId
        });

        return invited.InvitationToken!;
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
            // Evict first: a pooled connection whose search_path points at a dropped schema
            // must never be handed out again.
            _dataSourceFactory.Evict(_schemaName);

            await using var command = _dataSourceFactory.Root.CreateCommand(
                $"DROP SCHEMA IF EXISTS \"{_schemaName}\" CASCADE");
            await command.ExecuteNonQueryAsync();
        }

        if (_dataSourceFactory is not null)
            await _dataSourceFactory.DisposeAsync();
    }

    // ─── Arrange helpers ──────────────────────────────────────────────────────

    /// <summary>
    /// Seeds the master database with the minimum provisioning reads: the issuer company,
    /// its settings row pointing at this test's throwaway schema, and exactly one active
    /// VAT rate and currency.
    ///
    /// The single VAT rate and currency are what arm the bug: step 5 only clears a tenant
    /// code table when master has active rows for it, so without them a second run would
    /// be harmless for the uninteresting reason that it had nothing to copy.
    ///
    /// Number sequence formats and content templates are deliberately NOT seeded — master
    /// having none means the tenant keeps its migration-seeded defaults, which is what
    /// step 7 (default number sequences) needs to succeed.
    /// </summary>
    private void SeedMaster()
    {
        using var masterContext = CreateMasterContext();

        masterContext.Client.Add(new Client
        {
            Id = CompanyId,
            CompanyName = "Zavedená firma s.r.o.",
            RegistrationNumber = "19200000",
            IsIssuer = true,
            IsActive = true,
            IsVatPayer = true
        });

        masterContext.CompanySystemSettings.Add(new CompanySystemSettings
        {
            CompanyId = CompanyId,
            SchemaName = _schemaName,
            IsProvisioned = false,
            IsActive = false
        });

        masterContext.VatRate.Add(new VatRate
        {
            Name = "Základní 21 %",
            Rate = 21m,
            ValidFrom = new DateTime(2024, 1, 1, 0, 0, 0, DateTimeKind.Utc),
            IsDefault = true,
            IsActive = true
        });

        masterContext.Currency.Add(new Currency
        {
            Code = "CZK",
            Name = "Česká koruna",
            Symbol = "Kč",
            DecimalPlaces = 2,
            IsActive = true
        });

        masterContext.SaveChanges();
    }

    /// <summary>
    /// Builds a TenantDbContext bound to this test's throwaway schema, mirroring how
    /// TenantProvisioningService.CreateTenantContext builds the one it provisions with.
    /// A fresh context per call means assertions read from the database, not from a
    /// change tracker that still holds the objects provisioning just created.
    /// </summary>
    private TenantDbContext CreateTenantContext()
    {
        var options = new DbContextOptionsBuilder<TenantDbContext>()
            .UseNpgsql(_dataSourceFactory!.GetForSchema(_schemaName))
            // The tenant model is schema-dependent, so the model cache must be keyed by
            // schema — otherwise a context for another schema would reuse this model.
            .ReplaceService<IModelCacheKeyFactory, TenantModelCacheKeyFactory>()
            .Options;

        return new TenantDbContext(options) { Schema = _schemaName };
    }

    /// <summary>
    /// Issues one invoice in the tenant schema against the code table rows provisioning
    /// just copied there. This is what turns a re-run from "pointless" into "destructive":
    /// the DELETE in step 5 now hits rows a document points at.
    /// </summary>
    private async Task<(long VatRateId, long CurrencyId)> IssueInvoiceOnCodeTablesAsync()
    {
        await using var context = CreateTenantContext();

        var vatRate = await context.VatRate.SingleAsync();
        var currency = await context.Currency.SingleAsync();
        var issuer = await context.Client.SingleAsync(c => c.IsIssuer);

        context.Invoice.Add(new Invoice
        {
            DocumentType = EDocumentType.Invoice,
            Status = EInvoiceStatus.Completed,
            DocumentNumber = InvoiceDocumentNumber,
            IssueDate = DateTime.UtcNow,
            DueDate = DateTime.UtcNow.AddDays(14),
            IssuerId = issuer.Id,
            CurrencyId = currency.Id,
            TotalBeforeVat = 100m,
            TotalVat = 21m,
            TotalWithVat = 121m,
            InvoiceItem =
            [
                new InvoiceItem
                {
                    OrderIndex = 0,
                    Description = "Konzultace",
                    Quantity = 1m,
                    Unit = "hod",
                    UnitPrice = 100m,
                    VatRateId = vatRate.Id,
                    VatRatePercentage = 21m,
                    TotalBeforeVat = 100m,
                    VatAmount = 21m,
                    TotalWithVat = 121m
                }
            ]
        });

        await context.SaveChangesAsync();

        return (vatRate.Id, currency.Id);
    }

    // ─── Tests ────────────────────────────────────────────────────────────────

    /// <summary>
    /// Issue #192: an established company must not be re-provisioned. Before the fix, every
    /// invited colleague setting their password ran the whole pipeline again, and step 5
    /// cleared the code tables underneath the documents that reference them.
    ///
    /// The assertions deliberately check the Ids rather than just "the call did not throw":
    /// a DELETE + re-INSERT that somehow got past the foreign keys would restore the same
    /// VAT rate under a NEW id, which is the silent half of the bug.
    /// </summary>
    [SkippableFact]
    public async Task ProvisionTenantAsync_CompanyAlreadyProvisioned_LeavesTheCodeTablesAndDocumentsUntouched()
    {
        Skip.IfNot(_databaseAvailable, SkipReason);

        // Arrange — the company is provisioned once, as registration does it ...
        (await ProvisionAsync()).ShouldBeTrue();

        // ... and then starts invoicing on the code tables it was given.
        var (vatRateId, currencyId) = await IssueInvoiceOnCodeTablesAsync();

        // Act — the second run, in a request of its own: what setting the password of an
        // invited colleague triggers.
        var result = await ProvisionAsync();

        // Assert — reported as done ...
        result.ShouldBeTrue();

        // ... and nothing in the tenant schema moved.
        await using var context = CreateTenantContext();

        var vatRate = await context.VatRate.SingleAsync();
        vatRate.Id.ShouldBe(vatRateId, "The VAT rate was deleted and re-inserted under a new id.");

        var currency = await context.Currency.SingleAsync();
        currency.Id.ShouldBe(currencyId, "The currency was deleted and re-inserted under a new id.");

        var invoice = await context.Invoice
            .Include(i => i.InvoiceItem)
            .SingleAsync(i => i.DocumentNumber == InvoiceDocumentNumber);

        invoice.CurrencyId.ShouldBe(currencyId);
        invoice.InvoiceItem.ShouldHaveSingleItem().VatRateId.ShouldBe(vatRateId);
    }

    /// <summary>
    /// The counterpart, and the reason the guard is a flag check and not "never run twice":
    /// a company whose provisioning failed half way is still marked <c>IsProvisioned = false</c>
    /// (the flag is written in step 8, last), so the retry must run the whole pipeline.
    /// Without this test, the fix could regress into blocking every retry.
    /// </summary>
    [SkippableFact]
    public async Task ProvisionTenantAsync_CompanyNotYetProvisioned_RunsTheWholePipeline()
    {
        Skip.IfNot(_databaseAvailable, SkipReason);

        // Act
        var result = await ProvisionAsync();

        // Assert — the schema really was built, not skipped
        result.ShouldBeTrue();

        await using var masterContext = CreateMasterContext();
        var settings = await masterContext.CompanySystemSettings.SingleAsync(s => s.CompanyId == CompanyId);
        settings.IsProvisioned.ShouldBeTrue();

        await using var context = CreateTenantContext();
        (await context.Client.CountAsync(c => c.IsIssuer)).ShouldBe(1);
        (await context.VatRate.SingleAsync()).Rate.ShouldBe(21m);
        (await context.NumberSequence.CountAsync()).ShouldBeGreaterThan(0);
    }

    /// <summary>
    /// The same guard, reached the way the reported bug reached it: an invited colleague of
    /// an established company opens the link from the invitation email and sets a password.
    /// <c>UserService.SetPasswordAsync</c> then calls provisioning — and that call is the one
    /// that used to re-seed the tenant code tables underneath the company's invoices.
    ///
    /// Why this test exists next to the one above, which calls the service directly: the
    /// caller swallows every provisioning exception (a failed workspace must not revoke a
    /// password that is already stored). A re-run therefore leaves no trace in the caller's
    /// answer, and the foreign keys would abort the DELETE before the data visibly moved.
    /// The spy is what makes the difference observable — it records that nothing ever asked
    /// for a data source on the tenant schema, i.e. the pipeline never started.
    /// </summary>
    [SkippableFact]
    public async Task SetPasswordAsync_InvitedColleagueOfAnEstablishedCompany_NeverOpensTheTenantSchema()
    {
        Skip.IfNot(_databaseAvailable, SkipReason);

        // Arrange — an established, invoicing company ...
        (await ProvisionAsync()).ShouldBeTrue();
        var (vatRateId, currencyId) = await IssueInvoiceOnCodeTablesAsync();

        // ... which invites a colleague.
        var invitationToken = await InviteColleagueAsync();

        var dataSourceFactorySpy = CreateFactorySpy();

        // Act — the colleague's own request: sets the password from the emailed link.
        SetPasswordResultDto result;

        await using (var masterContext = CreateMasterContext())
        {
            var userService = new UserService(
                masterContext,
                CreateAuthServiceStub(),
                CreateProvisioningService(masterContext, dataSourceFactorySpy),
                NullLogger<UserService>.Instance);

            result = await userService.SetPasswordAsync(new SetPasswordDto
            {
                Token = invitationToken,
                NewPassword = "Kolega-Heslo-192"
            });
        }

        // Assert — the colleague is let in and told the workspace is ready ...
        result.PasswordSet.ShouldBeTrue();
        result.WorkspaceReady.ShouldBeTrue();

        // ... without provisioning ever opening the tenant schema ...
        dataSourceFactorySpy.DidNotReceive().GetForSchema(Arg.Any<string>(), Arg.Any<bool>());

        // ... and with the invoiced code tables still carrying their original ids.
        await using var context = CreateTenantContext();

        (await context.VatRate.SingleAsync()).Id.ShouldBe(vatRateId);
        (await context.Currency.SingleAsync()).Id.ShouldBe(currencyId);

        var invoice = await context.Invoice
            .Include(i => i.InvoiceItem)
            .SingleAsync(i => i.DocumentNumber == InvoiceDocumentNumber);

        invoice.InvoiceItem.ShouldHaveSingleItem().VatRateId.ShouldBe(vatRateId);
    }
}

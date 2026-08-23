using System.Net;
using System.Net.Http.Json;
using Fakvio.Contracts.Dto.Readiness;
using Fakvio.Domain.Entities;
using Fakvio.Domain.Enums;
using Fakvio.Infrastructure.Data;
using Fakvio.Tests.Integration.Fixtures;
using Fakvio.Tests.Integration.Helpers;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;

namespace Fakvio.Tests.Integration;

/// <summary>
/// Endpoint tests for <c>GET /api/readiness</c> (issue #209) — the REST face of
/// <c>ITenantReadinessService</c> (issue #205, DEVGUIDE §4.12).
///
/// Covered acceptance criteria:
///   - Unauthenticated request → 401 (the report exposes tenant settings, it is not public).
///   - Authenticated request → 200 with the report shape the UI banner (#215) consumes:
///     code, severity, missingFields, fixRoute, issuerId, issuerName, isReady.
///   - <c>?issuerId=</c> narrows the report to that one issuer.
///   - <c>?issuerId=</c> pointing at an issuer that does not exist → 404, NOT a report
///     saying ISSUER_MISSING. The service cannot tell "no issuer at all" from "that ID is
///     unknown" and reports both as ISSUER_MISSING by design; turning the second case into
///     a 404 is the controller's one job (see ReadinessController.GetReport).
///   - A tenant with no issuer at all and NO issuerId filter still gets 200 + ISSUER_MISSING —
///     the pair that pins the 404 to the filtered case only.
///
/// Uses FakvioFactory (WebApplicationFactory + InMemoryDatabase) — no real database.
/// Seeding runs in its own DI scope which is disposed before the request, so EF's change
/// tracker cannot fake populated navigation collections (the #104/#106 false-green trap).
/// </summary>
public class ReadinessEndpointTests : IClassFixture<FakvioFactory>
{
    private readonly FakvioFactory _factory;

    // Company impersonated by the SysAdmin test client. Chosen to avoid collision with
    // EPO tests (42, 43) and ReverseChargeCode tests (501).
    private const long TestCompanyId = 601L;

    // The tenant's two issuers: one deliberately incomplete, one fully configured.
    // Two of them are what makes the ?issuerId= filter observable at all.
    private const long IncompleteIssuerId = 6001L;
    private const long CompleteIssuerId   = 6002L;

    private const string IncompleteIssuerName = "Nedodělaná s.r.o.";

    // An ID that is never seeded — the 404 case.
    private const long UnknownIssuerId = 999999L;

    public ReadinessEndpointTests(FakvioFactory factory)
    {
        _factory = factory;
        _factory.InitializeDatabase();
    }

    // ── Authorization ─────────────────────────────────────────────────────────

    [Fact]
    public async Task GetReport_WithoutAuth_Returns401()
    {
        var client = _factory.CreateClient();
        // No Authorization header set.

        var response = await client.GetAsync("/api/readiness");

        response.StatusCode.ShouldBe(HttpStatusCode.Unauthorized,
            "GET /api/readiness must require authentication ([Authorize] on the controller).");
    }

    // ── 200: report shape ─────────────────────────────────────────────────────

    [Fact]
    public async Task GetReport_Authenticated_Returns200()
    {
        SeedAll();
        var client = await CreateImpersonatingClientAsync();

        var response = await client.GetAsync("/api/readiness");

        response.StatusCode.ShouldBe(HttpStatusCode.OK,
            "A tenant with unfinished settings is a valid report, not an HTTP error.");
    }

    /// <summary>
    /// Pins the wire shape the UI banner reads. Asserted on the raw JSON because
    /// <c>ReadinessReportDto.IsReady</c> is a computed getter — deserializing into the DTO
    /// would recompute it locally and never notice if the API stopped sending it.
    /// </summary>
    [Fact]
    public async Task GetReport_ReturnsIssueFieldsTheUiNeeds()
    {
        SeedAll();
        var client = await CreateImpersonatingClientAsync();

        var json = await client.GetStringAsync("/api/readiness");

        json.Contains("\"isReady\":false", StringComparison.Ordinal).ShouldBeTrue(
            "The incomplete issuer produces a blocking issue, so the API must send isReady=false. " +
            $"Actual payload: {json}");

        var report = await client.GetFromJsonAsync<ReadinessReportDto>("/api/readiness");
        report.ShouldNotBeNull();

        var issue = report.Issues.Single(i =>
            i.Code == ReadinessCodes.IssuerBankAccountMissing && i.IssuerId == IncompleteIssuerId);
        issue.Severity.ShouldBe(EReadinessSeverity.Blocking);
        issue.MissingFields.ShouldBe([nameof(Client.BankAccount)]);
        issue.FixRoute.ShouldBe("/my-company");
        issue.IssuerName.ShouldBe(IncompleteIssuerName,
            "The UI must be able to name the incomplete issuer without a second round-trip.");
    }

    // ── 200: ?issuerId= filter ────────────────────────────────────────────────

    [Fact]
    public async Task GetReport_WithIssuerId_ReportsOnlyThatIssuer()
    {
        SeedAll();
        var client = await CreateImpersonatingClientAsync();

        var report = await client.GetFromJsonAsync<ReadinessReportDto>(
            $"/api/readiness?issuerId={CompleteIssuerId}");

        report.ShouldNotBeNull();
        report.Issues.ShouldNotContain(i => i.IssuerId == IncompleteIssuerId,
            "?issuerId= must exclude the other issuer's problems.");
        report.Issues.ShouldNotContain(i => i.Severity == EReadinessSeverity.Blocking,
            "The complete issuer has nothing blocking — only the tenant-wide EPO warning is left.");
    }

    // ── 404 vs 200: the ISSUER_MISSING split ──────────────────────────────────

    [Fact]
    public async Task GetReport_WithUnknownIssuerId_Returns404()
    {
        SeedAll();
        var client = await CreateImpersonatingClientAsync();

        var response = await client.GetAsync($"/api/readiness?issuerId={UnknownIssuerId}");

        response.StatusCode.ShouldBe(HttpStatusCode.NotFound,
            "An explicit issuerId that does not exist is a bad request target, " +
            "not a tenant whose settings are unfinished.");
    }

    /// <summary>
    /// The other half of the pair above: the SAME ISSUER_MISSING code must stay a plain 200
    /// report when the caller did not name an issuer. Without this test the 404 mapping could
    /// be widened to every ISSUER_MISSING and nothing would go red.
    ///
    /// Needs a tenant with no issuer at all, so it runs against its own throwaway host
    /// instead of the shared fixture (whose tenant DB is seeded by the tests above).
    /// </summary>
    [Fact]
    public async Task GetReport_TenantWithoutAnyIssuer_Returns200WithIssuerMissing()
    {
        using var emptyTenantFactory = new FakvioFactory();
        emptyTenantFactory.InitializeDatabase();
        SeedCompanyOnly(emptyTenantFactory);

        var client = emptyTenantFactory.CreateClient();
        var login = await AuthHelper.LoginAsSysAdminAsync(client);
        AuthHelper.SetAuthToken(client, login.Token);
        AuthHelper.SetImpersonation(client, TestCompanyId);

        var response = await client.GetAsync("/api/readiness");

        response.StatusCode.ShouldBe(HttpStatusCode.OK,
            "Without an explicit issuerId, ISSUER_MISSING means 'your tenant is empty' — " +
            "that is a report, not a 404.");

        var report = await response.Content.ReadFromJsonAsync<ReadinessReportDto>();
        report.ShouldNotBeNull();
        report.Issues.ShouldContain(i => i.Code == ReadinessCodes.IssuerMissing);
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    /// <summary>
    /// Creates an HttpClient authenticated as SysAdmin and impersonating TestCompanyId.
    /// Readiness reads the tenant DB, so a tenant context is required — same pattern as
    /// ReverseChargeCodeControllerTests and the EPO endpoint tests.
    /// </summary>
    private async Task<HttpClient> CreateImpersonatingClientAsync()
    {
        var client = _factory.CreateClient();
        var login = await AuthHelper.LoginAsSysAdminAsync(client);
        AuthHelper.SetAuthToken(client, login.Token);
        AuthHelper.SetImpersonation(client, TestCompanyId);
        return client;
    }

    /// <summary>
    /// Seeds the company (master DB, needed for impersonation) and two tenant issuers.
    /// Idempotent — every test calls it.
    /// </summary>
    private void SeedAll()
    {
        SeedCompanyOnly(_factory);

        using var scope = _factory.Services.CreateScope();
        var tenantDb = scope.ServiceProvider.GetRequiredService<TenantDbContext>();

        if (tenantDb.Client.Any(c => c.Id == IncompleteIssuerId))
            return;

        // Issuer 1 — everything except a bank account. One missing field keeps the
        // assertions readable; the rule matrix itself is covered by TenantReadinessServiceTests.
        tenantDb.Client.Add(new Client
        {
            Id                 = IncompleteIssuerId,
            CompanyName        = IncompleteIssuerName,
            RegistrationNumber = "60010001",
            TaxNumber          = "CZ60010001",
            IsVatPayer         = true,
            IsIssuer           = true,
            IsActive           = true,
            Address =
            {
                new Address
                {
                    Id = 6101, AddressType = EAddressType.Primary, IsPrimary = true,
                    Street = "Hlavní 1", City = "Praha", PostalCode = "11000", Country = "CZ"
                }
            }
        });

        // Issuer 2 — fully configured, so ?issuerId= has something clean to return.
        tenantDb.Client.Add(new Client
        {
            Id                 = CompleteIssuerId,
            CompanyName        = "Hotová s.r.o.",
            RegistrationNumber = "60020002",
            TaxNumber          = "CZ60020002",
            IsVatPayer         = true,
            IsIssuer           = true,
            IsActive           = true,
            Address =
            {
                new Address
                {
                    Id = 6102, AddressType = EAddressType.Primary, IsPrimary = true,
                    Street = "Vedlejší 2", City = "Brno", PostalCode = "60200", Country = "CZ"
                }
            },
            BankAccount =
            {
                new BankAccount { Id = 6103, AccountNumber = "1234567890/0100" }
            }
        });

        tenantDb.SaveChanges();
    }

    /// <summary>
    /// Seeds only the master-DB rows impersonation needs: the company and its provisioned,
    /// active settings row. EPO fields are left unset on purpose — the resulting
    /// EPO_HEADER_INCOMPLETE warning is what proves warnings do not make a tenant unready.
    /// </summary>
    private static void SeedCompanyOnly(FakvioFactory factory)
    {
        using var scope = factory.Services.CreateScope();
        var masterDb = scope.ServiceProvider.GetRequiredService<MasterDbContext>();

        if (!masterDb.Client.Any(c => c.Id == TestCompanyId))
        {
            masterDb.Client.Add(new Client
            {
                Id                 = TestCompanyId,
                CompanyName        = "Readiness Test Company",
                RegistrationNumber = "T0000601",
                TaxNumber          = "CZ00000601",
                IsIssuer           = true,
                IsActive           = true
            });
        }

        if (!masterDb.CompanySystemSettings.Any(s => s.CompanyId == TestCompanyId))
        {
            masterDb.CompanySystemSettings.Add(new CompanySystemSettings
            {
                CompanyId     = TestCompanyId,
                SchemaName    = $"tenant_{TestCompanyId}",
                IsProvisioned = true,
                IsActive      = true
            });
        }

        masterDb.SaveChanges();
    }
}

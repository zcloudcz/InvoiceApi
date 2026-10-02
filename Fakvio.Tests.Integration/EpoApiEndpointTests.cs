using System.Net;
using System.Text.Json;
using System.Xml.Linq;
using Fakvio.Domain.Entities;
using Fakvio.Domain.Enums;
using Fakvio.Infrastructure.Data;
using Fakvio.Tests.Integration.Fixtures;
using Fakvio.Tests.Integration.Helpers;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;

namespace Fakvio.Tests.Integration;

/// <summary>
/// Integration tests for EPO export endpoints (#39):
///   GET /api/vat-report/epo/return?year=&amp;period=&amp;type=
///   GET /api/vat-report/epo/control-statement?year=&amp;period=&amp;type=
///
/// Test scenarios:
///   1. Unauthenticated request → 401 Unauthorized.
///   2. Missing EPO header settings (no CompanySystemSettings) → 400 EPO_HEADER_INCOMPLETE.
///   3. All EPO settings present → 200 OK with valid XML and correct Content-Disposition filename.
///   4. Monthly filename format: DPHDP3_2026_M04.xml / DPHKH1_2026_M04.xml.
///   5. Quarterly filename format: DPHDP3_2026_Q2.xml / DPHKH1_2026_Q2.xml.
///   6. Invalid period argument → 400 INVALID_ARGUMENT.
/// </summary>
public class EpoApiEndpointTests : IClassFixture<FakvioFactory>
{
    private readonly FakvioFactory _factory;

    // Tests that need EPO settings present use CompanyId 42.
    // Tests that need EPO settings ABSENT use CompanyId 43 to avoid cross-test pollution:
    // the factory is shared (IClassFixture), so the InMemoryDB persists across all tests
    // in this class — separate IDs guarantee isolation.
    // Non-VAT-payer 403 tests live in EpoNonVatPayerTests (own factory instance)
    // because the shared TenantDbContext can only have one "first active issuer."
    private const long TestCompanyId      = 42L;
    private const long TestCompanyIdNoEpo = 43L;

    public EpoApiEndpointTests(FakvioFactory factory)
    {
        _factory = factory;
        _factory.InitializeDatabase();
    }

    // =========================================================================
    // Helpers — seed tenant data
    // =========================================================================

    /// <summary>
    /// Seeds a Company (Client), CompanySystemSettings, and (optionally) EPO header data
    /// into the InMemoryDatabase so the EPO endpoints have data to work with.
    ///
    /// The SysAdmin impersonates <paramref name="companyId"/> via X-Company-Id header,
    /// so this company must be provisioned + active.
    /// </summary>
    private void SeedTenantWithEpoSettings(long companyId, bool includeEpoSettings = true)
    {
        using var scope = _factory.Services.CreateScope();
        var masterDb = scope.ServiceProvider.GetRequiredService<MasterDbContext>();
        var tenantDb = scope.ServiceProvider.GetRequiredService<TenantDbContext>();

        // Add the company to master DB (as issuer).
        if (!masterDb.Client.Any(c => c.Id == companyId))
        {
            masterDb.Client.Add(new Client
            {
                Id = companyId,
                CompanyName = $"Test Company {companyId}",
                RegistrationNumber = $"T{companyId:D7}",
                TaxNumber = $"CZ{companyId:D8}",
                IsIssuer = true,
                IsActive = true
            });
        }

        // Add CompanySystemSettings to master DB.
        if (!masterDb.CompanySystemSettings.Any(s => s.CompanyId == companyId))
        {
            var settings = new CompanySystemSettings
            {
                CompanyId    = companyId,
                SchemaName   = $"tenant_{companyId}",
                IsProvisioned = true,
                IsActive     = true,
                // EPO header fields (issue #39) — only set when requested.
                EpoTaxOfficeCode        = includeEpoSettings ? 451 : (int?)null,
                EpoTaxOfficeBranchCode  = includeEpoSettings ? 2017 : (int?)null,
                EpoContactPhone         = includeEpoSettings ? "+420 123 456 789" : null,
                EpoContactEmail         = includeEpoSettings ? "tax@company.cz" : null,
                EpoAuthorizedPersonName = includeEpoSettings ? "Ing. Jan Novák" : null
            };
            masterDb.CompanySystemSettings.Add(settings);
        }

        masterDb.SaveChanges();

        // Add the issuer to the tenant DB (VatReportService needs it via TenantDbContext).
        var czk = tenantDb.Currency.FirstOrDefault();
        if (!tenantDb.Client.Any(c => c.IsIssuer))
        {
            tenantDb.Client.Add(new Client
            {
                Id = companyId,
                CompanyName = $"Test Company {companyId}",
                RegistrationNumber = $"T{companyId:D7}",
                TaxNumber = $"CZ{companyId:D8}",
                IsIssuer = true,
                IsActive = true,
                IsVatPayer = true
            });
            tenantDb.SaveChanges();
        }
    }

    // =========================================================================
    // 1. Unauthenticated → 401
    // =========================================================================

    [Fact]
    public async Task EpoReturn_Unauthenticated_Returns401()
    {
        var client = _factory.CreateClient();
        // No Authorization header.

        var response = await client.GetAsync("/api/vat-report/epo/return?year=2026&period=3&type=Monthly");

        response.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task EpoControlStatement_Unauthenticated_Returns401()
    {
        var client = _factory.CreateClient();

        var response = await client.GetAsync("/api/vat-report/epo/control-statement?year=2026&period=3&type=Monthly");

        response.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
    }

    // =========================================================================
    // 2. Missing EPO settings → 400 EPO_HEADER_INCOMPLETE
    // =========================================================================

    [Fact]
    public async Task EpoReturn_MissingEpoSettings_Returns400WithCode_EpoHeaderIncomplete()
    {
        // Seed a separate company (ID 43) WITHOUT EPO header fields.
        // Using a distinct ID avoids cross-test pollution: the factory is shared across
        // all tests, so seeding company 42 without settings would conflict with tests
        // that seed company 42 WITH settings.
        SeedTenantWithEpoSettings(TestCompanyIdNoEpo, includeEpoSettings: false);

        var client = _factory.CreateClient();
        var loginResponse = await AuthHelper.LoginAsSysAdminAsync(client);
        AuthHelper.SetAuthToken(client, loginResponse.Token);
        AuthHelper.SetImpersonation(client, TestCompanyIdNoEpo);

        var response = await client.GetAsync("/api/vat-report/epo/return?year=2026&period=3&type=Monthly");

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);

        var body = await response.Content.ReadAsStringAsync();
        using var doc = JsonDocument.Parse(body);
        doc.RootElement.GetProperty("code").GetString()
            .ShouldBe("EPO_HEADER_INCOMPLETE",
                "Response body must contain machine-readable code EPO_HEADER_INCOMPLETE.");

        // Both missing fields must be listed.
        var missingFields = doc.RootElement.GetProperty("missingFields")
            .EnumerateArray()
            .Select(e => e.GetString())
            .ToList();
        missingFields.ShouldContain("EpoTaxOfficeCode");
        missingFields.ShouldContain("EpoTaxOfficeBranchCode");
    }

    [Fact]
    public async Task EpoControlStatement_MissingEpoSettings_Returns400WithCode_EpoHeaderIncomplete()
    {
        // Same isolation strategy as EpoReturn — use company 43 (no EPO settings).
        SeedTenantWithEpoSettings(TestCompanyIdNoEpo, includeEpoSettings: false);

        var client = _factory.CreateClient();
        var loginResponse = await AuthHelper.LoginAsSysAdminAsync(client);
        AuthHelper.SetAuthToken(client, loginResponse.Token);
        AuthHelper.SetImpersonation(client, TestCompanyIdNoEpo);

        var response = await client.GetAsync("/api/vat-report/epo/control-statement?year=2026&period=3&type=Monthly");

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);

        var body = await response.Content.ReadAsStringAsync();
        using var doc = JsonDocument.Parse(body);
        doc.RootElement.GetProperty("code").GetString().ShouldBe("EPO_HEADER_INCOMPLETE");
    }

    // =========================================================================
    // 3. Valid settings → 200 OK with XML
    // =========================================================================

    [Fact]
    public async Task EpoReturn_WithValidSettings_Returns200AndXmlContent()
    {
        SeedTenantWithEpoSettings(TestCompanyId, includeEpoSettings: true);

        var client = _factory.CreateClient();
        var loginResponse = await AuthHelper.LoginAsSysAdminAsync(client);
        AuthHelper.SetAuthToken(client, loginResponse.Token);
        AuthHelper.SetImpersonation(client, TestCompanyId);

        var response = await client.GetAsync("/api/vat-report/epo/return?year=2026&period=3&type=Monthly");

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        response.Content.Headers.ContentType?.MediaType.ShouldBe("application/xml");

        var xml = await response.Content.ReadAsStringAsync();
        xml.ShouldContain("<DPHDP3>");
        xml.ShouldContain("VetaD");
        xml.ShouldContain("VetaP");

        // EPO settings must be reflected in the VetaP attributes.
        var doc = XDocument.Parse(xml);
        var vetaP = doc.Descendants("VetaP").Single();
        vetaP.Attribute("c_ufo")!.Value.ShouldBe("451");
        vetaP.Attribute("c_pracufo")!.Value.ShouldBe("2017");
    }

    [Fact]
    public async Task EpoControlStatement_WithValidSettings_Returns200AndXmlContent()
    {
        SeedTenantWithEpoSettings(TestCompanyId, includeEpoSettings: true);

        var client = _factory.CreateClient();
        var loginResponse = await AuthHelper.LoginAsSysAdminAsync(client);
        AuthHelper.SetAuthToken(client, loginResponse.Token);
        AuthHelper.SetImpersonation(client, TestCompanyId);

        var response = await client.GetAsync("/api/vat-report/epo/control-statement?year=2026&period=3&type=Monthly");

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        response.Content.Headers.ContentType?.MediaType.ShouldBe("application/xml");

        var xml = await response.Content.ReadAsStringAsync();
        xml.ShouldContain("<DPHKH1>");
    }

    // =========================================================================
    // 4. Monthly filename format
    // =========================================================================

    [Fact]
    public async Task EpoReturn_Monthly_ContentDispositionFilenameIsCorrect()
    {
        SeedTenantWithEpoSettings(TestCompanyId, includeEpoSettings: true);

        var client = _factory.CreateClient();
        var loginResponse = await AuthHelper.LoginAsSysAdminAsync(client);
        AuthHelper.SetAuthToken(client, loginResponse.Token);
        AuthHelper.SetImpersonation(client, TestCompanyId);

        var response = await client.GetAsync("/api/vat-report/epo/return?year=2026&period=4&type=Monthly");

        response.StatusCode.ShouldBe(HttpStatusCode.OK);

        // Content-Disposition must contain the expected filename.
        var contentDisposition = response.Content.Headers.ContentDisposition;
        contentDisposition.ShouldNotBeNull("Content-Disposition header must be present.");
        contentDisposition!.FileNameStar.ShouldBe("DPHDP3_2026_M04.xml",
            "Monthly DPHDP3 filename must follow DPHDP3_{year}_M{mm:D2}.xml pattern.");
    }

    [Fact]
    public async Task EpoControlStatement_Monthly_ContentDispositionFilenameIsCorrect()
    {
        SeedTenantWithEpoSettings(TestCompanyId, includeEpoSettings: true);

        var client = _factory.CreateClient();
        var loginResponse = await AuthHelper.LoginAsSysAdminAsync(client);
        AuthHelper.SetAuthToken(client, loginResponse.Token);
        AuthHelper.SetImpersonation(client, TestCompanyId);

        var response = await client.GetAsync("/api/vat-report/epo/control-statement?year=2026&period=4&type=Monthly");

        response.StatusCode.ShouldBe(HttpStatusCode.OK);

        var contentDisposition = response.Content.Headers.ContentDisposition;
        contentDisposition.ShouldNotBeNull();
        contentDisposition!.FileNameStar.ShouldBe("DPHKH1_2026_M04.xml");
    }

    // =========================================================================
    // 5. Quarterly filename format
    // =========================================================================

    [Fact]
    public async Task EpoReturn_Quarterly_ContentDispositionFilenameIsCorrect()
    {
        SeedTenantWithEpoSettings(TestCompanyId, includeEpoSettings: true);

        var client = _factory.CreateClient();
        var loginResponse = await AuthHelper.LoginAsSysAdminAsync(client);
        AuthHelper.SetAuthToken(client, loginResponse.Token);
        AuthHelper.SetImpersonation(client, TestCompanyId);

        var response = await client.GetAsync("/api/vat-report/epo/return?year=2026&period=2&type=Quarterly");

        response.StatusCode.ShouldBe(HttpStatusCode.OK);

        var contentDisposition = response.Content.Headers.ContentDisposition;
        contentDisposition.ShouldNotBeNull();
        contentDisposition!.FileNameStar.ShouldBe("DPHDP3_2026_Q2.xml",
            "Quarterly DPHDP3 filename must follow DPHDP3_{year}_Q{q}.xml pattern.");
    }

    [Fact]
    public async Task EpoControlStatement_Quarterly_ContentDispositionFilenameIsCorrect()
    {
        SeedTenantWithEpoSettings(TestCompanyId, includeEpoSettings: true);

        var client = _factory.CreateClient();
        var loginResponse = await AuthHelper.LoginAsSysAdminAsync(client);
        AuthHelper.SetAuthToken(client, loginResponse.Token);
        AuthHelper.SetImpersonation(client, TestCompanyId);

        var response = await client.GetAsync("/api/vat-report/epo/control-statement?year=2026&period=2&type=Quarterly");

        response.StatusCode.ShouldBe(HttpStatusCode.OK);

        var contentDisposition = response.Content.Headers.ContentDisposition;
        contentDisposition.ShouldNotBeNull();
        contentDisposition!.FileNameStar.ShouldBe("DPHKH1_2026_Q2.xml");
    }

    // =========================================================================
    // 6. Invalid period → 400 INVALID_ARGUMENT
    // =========================================================================

    [Fact]
    public async Task EpoReturn_InvalidPeriod_Returns400InvalidArgument()
    {
        SeedTenantWithEpoSettings(TestCompanyId, includeEpoSettings: true);

        var client = _factory.CreateClient();
        var loginResponse = await AuthHelper.LoginAsSysAdminAsync(client);
        AuthHelper.SetAuthToken(client, loginResponse.Token);
        AuthHelper.SetImpersonation(client, TestCompanyId);

        // Period 0 is out of range for any period type.
        var response = await client.GetAsync("/api/vat-report/epo/return?year=2026&period=0&type=Monthly");

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);

        var body = await response.Content.ReadAsStringAsync();
        using var doc = JsonDocument.Parse(body);
        doc.RootElement.GetProperty("code").GetString().ShouldBe("INVALID_ARGUMENT");
    }

    // Note: non-VAT payer 403 VAT_PAYER_REQUIRED tests are in EpoNonVatPayerTests.cs
    // (own FakvioFactory instance) because this shared factory's TenantDbContext can
    // only have one "first active issuer" — a requirement at odds with testing IsVatPayer=false
    // while other tests in this class need IsVatPayer=true.

    // =========================================================================
    // 7. DPHSHV (EU summary statement)
    // =========================================================================

    [Fact]
    public async Task EpoSummaryStatement_Unauthenticated_Returns401()
    {
        var client = _factory.CreateClient();

        (await client.GetAsync("/api/vat-report/epo/summary-statement?year=2026&period=3&type=Monthly"))
            .StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        (await client.GetAsync("/api/vat-report/epo/summary-statement/preview?year=2026&period=3&type=Monthly"))
            .StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task EpoSummaryStatementPreview_NoEuInvoices_ReturnsEmptyList()
    {
        SeedTenantWithEpoSettings(TestCompanyId, includeEpoSettings: true);
        var client = _factory.CreateClient();
        AuthHelper.SetAuthToken(client, (await AuthHelper.LoginAsSysAdminAsync(client)).Token);
        AuthHelper.SetImpersonation(client, TestCompanyId);

        var response = await client.GetAsync("/api/vat-report/epo/summary-statement/preview?year=2026&period=3&type=Monthly&goods=DE123456789");

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement.GetArrayLength().ShouldBe(0);
    }

    [Fact]
    public async Task EpoSummaryStatement_NothingToReport_Returns400WithCode()
    {
        SeedTenantWithEpoSettings(TestCompanyId, includeEpoSettings: true);
        var client = _factory.CreateClient();
        AuthHelper.SetAuthToken(client, (await AuthHelper.LoginAsSysAdminAsync(client)).Token);
        AuthHelper.SetImpersonation(client, TestCompanyId);

        var response = await client.GetAsync("/api/vat-report/epo/summary-statement?year=2026&period=3&type=Monthly");

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement
            .GetProperty("code").GetString().ShouldBe("CONFIGURATION_ERROR");
    }

    [Fact]
    public async Task EpoSummaryStatement_InvalidPeriod_Returns400()
    {
        SeedTenantWithEpoSettings(TestCompanyId, includeEpoSettings: true);
        var client = _factory.CreateClient();
        AuthHelper.SetAuthToken(client, (await AuthHelper.LoginAsSysAdminAsync(client)).Token);
        AuthHelper.SetImpersonation(client, TestCompanyId);

        var response = await client.GetAsync("/api/vat-report/epo/summary-statement?year=2026&period=13&type=Monthly");

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
    }
}

using System.Net;
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
/// Verifies that the smoke test seed data (company CZ12345678 + one invoice in 2026/M01)
/// produces valid DPHDP3 and DPHKH1 XML via the API.
///
/// These are the same checks the EPO sandbox smoke tests (EpoSandboxSmokeTests) perform
/// on the local API before forwarding to the external sandbox. By running them here we
/// confirm the seed data is correct and the XML generation succeeds — without needing
/// the external call or the RUN_EPO_SANDBOX_TESTS env variable.
///
/// Company ID 9999 and client ID 9998 mirror the constants in EpoSandboxSmokeTests
/// exactly, so any seed change breaks both test classes symmetrically.
/// </summary>
public class EpoSmokeDataGenerationTests : IClassFixture<FakvioFactory>
{
    // Mirror of constants in EpoSandboxSmokeTests — must stay in sync.
    private const long SmokeCompanyId  = 9999L;
    private const long SmokeClientId   = 9998L;
    private const long SmokeInvoiceId  = 99001L;
    private const string SmokeDic      = "CZ12345678";
    private const string SmokeClientDic = "CZ87654321";

    private readonly FakvioFactory _factory;

    public EpoSmokeDataGenerationTests(FakvioFactory factory)
    {
        _factory = factory;
        _factory.InitializeDatabase();
        SeedSmokeCompanyData();
    }

    // =========================================================================
    // Test data seeding — identical to EpoSandboxSmokeTests.SeedSmokeTestData()
    // =========================================================================

    /// <summary>
    /// Replicates the seed from EpoSandboxSmokeTests so that API calls to company 9999
    /// for period 2026/M01 succeed in a standard integration test run.
    ///
    /// Keeps the two seed blocks in sync: if EpoSandboxSmokeTests changes its seed,
    /// failing tests here will catch the divergence.
    /// </summary>
    private void SeedSmokeCompanyData()
    {
        using var scope = _factory.Services.CreateScope();
        var masterDb = scope.ServiceProvider.GetRequiredService<MasterDbContext>();
        var tenantDb = scope.ServiceProvider.GetRequiredService<TenantDbContext>();

        // ── Master DB: issuer + EPO header settings ────────────────────────
        if (!masterDb.Client.Any(c => c.Id == SmokeCompanyId))
        {
            masterDb.Client.Add(new Client
            {
                Id                 = SmokeCompanyId,
                CompanyName        = "Smoke Test Company s.r.o.",
                RegistrationNumber = "12345678",
                TaxNumber          = SmokeDic,
                IsIssuer           = true,
                IsActive           = true
            });
        }

        if (!masterDb.CompanySystemSettings.Any(s => s.CompanyId == SmokeCompanyId))
        {
            masterDb.CompanySystemSettings.Add(new CompanySystemSettings
            {
                CompanyId               = SmokeCompanyId,
                SchemaName              = $"tenant_{SmokeCompanyId}",
                IsProvisioned           = true,
                IsActive                = true,
                EpoTaxOfficeCode        = 451,
                EpoTaxOfficeBranchCode  = 2017,
                EpoContactPhone         = "+420 123 456 789",
                EpoContactEmail         = "test@fakvio.cz",
                EpoAuthorizedPersonName = "Jan Novák"
            });
        }

        masterDb.SaveChanges();

        // ── Tenant DB: issuer ──────────────────────────────────────────────
        if (!tenantDb.Client.Any(c => c.Id == SmokeCompanyId))
        {
            tenantDb.Client.Add(new Client
            {
                Id                 = SmokeCompanyId,
                CompanyName        = "Smoke Test Company s.r.o.",
                RegistrationNumber = "12345678",
                TaxNumber          = SmokeDic,
                IsIssuer           = true,
                IsActive           = true,
                IsVatPayer         = true
            });
            tenantDb.SaveChanges();
        }

        // ── Tenant DB: client (invoice recipient) ──────────────────────────
        if (!tenantDb.Client.Any(c => c.Id == SmokeClientId))
        {
            tenantDb.Client.Add(new Client
            {
                Id                 = SmokeClientId,
                CompanyName        = "Smoke Test Client a.s.",
                RegistrationNumber = "87654321",
                TaxNumber          = SmokeClientDic,
                IsIssuer           = false,
                IsActive           = true,
                IsVatPayer         = true
            });
            tenantDb.SaveChanges();
        }

        // ── Tenant DB: one invoice in 2026/M01 ────────────────────────────
        if (!tenantDb.Invoice.Any(i => i.Id == SmokeInvoiceId))
        {
            var czk = tenantDb.Currency.FirstOrDefault(c => c.Code == "CZK")
                   ?? tenantDb.Currency.First();

            tenantDb.Invoice.Add(new Invoice
            {
                Id                = SmokeInvoiceId,
                DocumentNumber    = "FV-2026-0001",
                DocumentType      = EDocumentType.Invoice,
                Status            = EInvoiceStatus.Completed,
                ClientId          = SmokeClientId,
                TaxableSupplyDate = DateTime.SpecifyKind(new DateTime(2026, 1, 15), DateTimeKind.Utc),
                IssueDate         = DateTime.SpecifyKind(new DateTime(2026, 1, 15), DateTimeKind.Utc),
                DueDate           = DateTime.SpecifyKind(new DateTime(2026, 2, 15), DateTimeKind.Utc),
                TotalBeforeVat    = 10_000m,
                TotalVat          = 2_100m,
                TotalWithVat      = 12_100m,
                CurrencyId        = czk.Id,
                InvoiceItem       =
                [
                    new InvoiceItem
                    {
                        Description       = "Smoke test service",
                        Quantity          = 1,
                        UnitPrice         = 10_000m,
                        VatRatePercentage = 21m,
                        TotalBeforeVat    = 10_000m,
                        VatAmount         = 2_100m,
                        TotalWithVat      = 12_100m
                    }
                ]
            });
            tenantDb.SaveChanges();
        }
    }

    // =========================================================================
    // DPHDP3 generation for smoke company
    // =========================================================================

    /// <summary>
    /// Verifies that the smoke company (CZ12345678) with full EPO settings returns HTTP 200
    /// and valid DPHDP3 XML for period 2026/M01.
    ///
    /// This is the same API call that EpoSandboxSmokeTests makes before posting to the
    /// external sandbox — confirming the local generation succeeds independently.
    /// </summary>
    [Fact]
    public async Task SmokeCompany_DphdP3_Monthly_Returns200AndWellFormedXml()
    {
        var client = _factory.CreateClient();
        var loginResponse = await AuthHelper.LoginAsSysAdminAsync(client);
        AuthHelper.SetAuthToken(client, loginResponse.Token);
        AuthHelper.SetImpersonation(client, SmokeCompanyId);

        var response = await client.GetAsync(
            "/api/vat-report/epo/return?year=2026&period=1&type=Monthly");

        response.StatusCode.ShouldBe(HttpStatusCode.OK,
            $"EPO return endpoint must return 200. Body: {await response.Content.ReadAsStringAsync()}");
        response.Content.Headers.ContentType?.MediaType.ShouldBe("application/xml");

        var xml = await response.Content.ReadAsStringAsync();
        var doc = XDocument.Parse(xml); // throws on malformed XML — asserts well-formed

        // Root structure must be DPHDP3 (inside Pisemnost).
        doc.Descendants("DPHDP3").FirstOrDefault()
            .ShouldNotBeNull("Response XML must contain <DPHDP3> element.");
    }

    /// <summary>
    /// Verifies the DPHDP3 VetaP element carries the smoke company's EPO header values
    /// (c_ufo=451, c_pracufo=2017) and the numeric-only DIC (EPO strips the "CZ" prefix).
    /// </summary>
    [Fact]
    public async Task SmokeCompany_DphdP3_VetaP_ContainsCorrectEpoHeaderValues()
    {
        var client = _factory.CreateClient();
        var loginResponse = await AuthHelper.LoginAsSysAdminAsync(client);
        AuthHelper.SetAuthToken(client, loginResponse.Token);
        AuthHelper.SetImpersonation(client, SmokeCompanyId);

        var response = await client.GetAsync(
            "/api/vat-report/epo/return?year=2026&period=1&type=Monthly");

        response.StatusCode.ShouldBe(HttpStatusCode.OK);

        var xml = await response.Content.ReadAsStringAsync();
        var doc = XDocument.Parse(xml);
        var vetaP = doc.Descendants("VetaP").FirstOrDefault();
        vetaP.ShouldNotBeNull("DPHDP3 must contain <VetaP> element.");

        // Tax office code must be ≤ 3 digits (XSD totalDigits=3).
        vetaP!.Attribute("c_ufo")!.Value.ShouldBe("451");
        vetaP.Attribute("c_pracufo")!.Value.ShouldBe("2017");

        // EPO strips the "CZ" country prefix from TaxNumber: "CZ12345678" → "12345678".
        // This is EPO XSD conformance — dic attribute must be numeric digits only.
        vetaP.Attribute("dic")!.Value.ShouldBe("12345678");
    }

    /// <summary>
    /// Verifies the DPHDP3 VetaD element carries the correct period metadata
    /// (rok=2026, mesic=1) matching the requested period 2026/M01.
    /// </summary>
    [Fact]
    public async Task SmokeCompany_DphdP3_VetaD_ContainsCorrectPeriodMetadata()
    {
        var client = _factory.CreateClient();
        var loginResponse = await AuthHelper.LoginAsSysAdminAsync(client);
        AuthHelper.SetAuthToken(client, loginResponse.Token);
        AuthHelper.SetImpersonation(client, SmokeCompanyId);

        var response = await client.GetAsync(
            "/api/vat-report/epo/return?year=2026&period=1&type=Monthly");

        response.StatusCode.ShouldBe(HttpStatusCode.OK);

        var xml = await response.Content.ReadAsStringAsync();
        var doc = XDocument.Parse(xml);
        var vetaD = doc.Descendants("VetaD").FirstOrDefault();
        vetaD.ShouldNotBeNull("DPHDP3 must contain <VetaD> element.");

        vetaD!.Attribute("rok")!.Value.ShouldBe("2026");
        vetaD.Attribute("mesic")!.Value.ShouldBe("1");
    }

    // =========================================================================
    // DPHKH1 generation for smoke company
    // =========================================================================

    /// <summary>
    /// Verifies that the smoke company returns HTTP 200 and valid DPHKH1 XML
    /// for period 2026/M01.
    /// </summary>
    [Fact]
    public async Task SmokeCompany_Dphkh1_Monthly_Returns200AndWellFormedXml()
    {
        var client = _factory.CreateClient();
        var loginResponse = await AuthHelper.LoginAsSysAdminAsync(client);
        AuthHelper.SetAuthToken(client, loginResponse.Token);
        AuthHelper.SetImpersonation(client, SmokeCompanyId);

        var response = await client.GetAsync(
            "/api/vat-report/epo/control-statement?year=2026&period=1&type=Monthly");

        response.StatusCode.ShouldBe(HttpStatusCode.OK,
            $"EPO control-statement endpoint must return 200. Body: {await response.Content.ReadAsStringAsync()}");
        response.Content.Headers.ContentType?.MediaType.ShouldBe("application/xml");

        var xml = await response.Content.ReadAsStringAsync();
        var doc = XDocument.Parse(xml);

        doc.Descendants("DPHKH1").FirstOrDefault()
            .ShouldNotBeNull("Response XML must contain <DPHKH1> element.");
    }

    /// <summary>
    /// Verifies that the seeded invoice (12 100 CZK incl. VAT, CZ client DIČ) appears
    /// in DPHKH1 section A.4 (output invoices ≥ 10 000 CZK incl. VAT with CZ DIČ).
    ///
    /// This is the key acceptance criterion from issue #41: the smoke invoice must
    /// cross the A.4 threshold so the DPHKH1 carries actual invoice data.
    /// </summary>
    [Fact]
    public async Task SmokeCompany_Dphkh1_SeededInvoice_AppearsInSectionA4()
    {
        var client = _factory.CreateClient();
        var loginResponse = await AuthHelper.LoginAsSysAdminAsync(client);
        AuthHelper.SetAuthToken(client, loginResponse.Token);
        AuthHelper.SetImpersonation(client, SmokeCompanyId);

        var response = await client.GetAsync(
            "/api/vat-report/epo/control-statement?year=2026&period=1&type=Monthly");

        response.StatusCode.ShouldBe(HttpStatusCode.OK);

        var xml = await response.Content.ReadAsStringAsync();
        var doc = XDocument.Parse(xml);

        // VetaA4 rows hold individual output invoices above the 10 000 CZK threshold.
        var vetaA4Rows = doc.Descendants("VetaA4").ToList();
        vetaA4Rows.ShouldNotBeEmpty(
            "DPHKH1 must contain at least one <VetaA4> row for the smoke invoice " +
            "(12 100 CZK incl. VAT, CZ client DIČ). " +
            "Check that the seeded invoice crosses the A.4 threshold.");

        // EPO strips the "CZ" prefix: "CZ87654321" → "87654321" in dic_odb attribute.
        // This is EPO XSD conformance — dic_odb must be numeric digits only.
        var dicValues = vetaA4Rows
            .Select(e => e.Attribute("dic_odb")?.Value)
            .Where(v => v != null)
            .ToList();
        dicValues.ShouldContain("87654321",
            $"VetaA4 rows must reference the numeric part of the client DIČ {SmokeClientDic} → \"87654321\".");
    }

    // =========================================================================
    // Content-Disposition filename for smoke period
    // =========================================================================

    /// <summary>
    /// Verifies Content-Disposition filename for DPHDP3 2026/M01 (smoke period).
    /// Expected: DPHDP3_2026_M01.xml.
    /// </summary>
    [Fact]
    public async Task SmokeCompany_DphdP3_SmokePeriod_ContentDispositionFilenameIsCorrect()
    {
        var client = _factory.CreateClient();
        var loginResponse = await AuthHelper.LoginAsSysAdminAsync(client);
        AuthHelper.SetAuthToken(client, loginResponse.Token);
        AuthHelper.SetImpersonation(client, SmokeCompanyId);

        var response = await client.GetAsync(
            "/api/vat-report/epo/return?year=2026&period=1&type=Monthly");

        response.StatusCode.ShouldBe(HttpStatusCode.OK);

        var contentDisposition = response.Content.Headers.ContentDisposition;
        contentDisposition.ShouldNotBeNull("Content-Disposition header must be present.");
        contentDisposition!.FileNameStar.ShouldBe("DPHDP3_2026_M01.xml",
            "Smoke period (M01) filename must follow DPHDP3_{year}_M{mm:D2}.xml pattern.");
    }

    /// <summary>
    /// Verifies Content-Disposition filename for DPHKH1 2026/M01 (smoke period).
    /// Expected: DPHKH1_2026_M01.xml.
    /// </summary>
    [Fact]
    public async Task SmokeCompany_Dphkh1_SmokePeriod_ContentDispositionFilenameIsCorrect()
    {
        var client = _factory.CreateClient();
        var loginResponse = await AuthHelper.LoginAsSysAdminAsync(client);
        AuthHelper.SetAuthToken(client, loginResponse.Token);
        AuthHelper.SetImpersonation(client, SmokeCompanyId);

        var response = await client.GetAsync(
            "/api/vat-report/epo/control-statement?year=2026&period=1&type=Monthly");

        response.StatusCode.ShouldBe(HttpStatusCode.OK);

        var contentDisposition = response.Content.Headers.ContentDisposition;
        contentDisposition.ShouldNotBeNull("Content-Disposition header must be present.");
        contentDisposition!.FileNameStar.ShouldBe("DPHKH1_2026_M01.xml",
            "Smoke period (M01) filename must follow DPHKH1_{year}_M{mm:D2}.xml pattern.");
    }
}

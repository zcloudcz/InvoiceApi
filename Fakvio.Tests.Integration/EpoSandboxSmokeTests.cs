using System.Net;
using System.Text;
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
/// Smoke tests that submit a minimal EPO XML document to the MFČR sandbox environment
/// (adisepo.mfcr.cz) and verify the portal accepts it (HTTP 200, no validation errors).
///
/// These tests perform REAL HTTP calls to an external system. They are:
/// - Guarded by the env variable RUN_EPO_SANDBOX_TESTS=true (skip unless set).
/// - Marked [SkippableFact]: if the sandbox responds 4xx/5xx or times out, the test
///   is skipped (not failed) — network fragility must not break the CI pipeline.
///
/// When to run: manually before a release, or in a dedicated integration CI job
/// with the environment variable set. See EPO-README.md for instructions.
///
/// EPO sandbox endpoint:
///   https://adisepo.mfcr.cz/adis/jepo/epo/ePodani/podani.faces
///
/// Request format: multipart/form-data, field name "soubor", single XML file.
/// The portal responds with an XML document containing either:
///   - &lt;OK /&gt; (or &lt;Pisemnost&gt; root without &lt;chyba&gt; / &lt;error&gt; children) — accepted.
///   - &lt;chyba&gt; / &lt;error&gt; elements — rejected (validation or format error).
///
/// Test data: synthetic company CZ12345678, one output invoice to CZ87654321, 12 100 CZK incl. VAT.
/// </summary>
public class EpoSandboxSmokeTests : IClassFixture<FakvioFactory>
{
    // EPO sandbox endpoint for anonymous (no-auth) XML upload.
    // This URL is the publicly documented EPO test environment.
    // See: https://adisspr.mfcr.cz/adistc/adis/idpr_pub/epo2_info/popis_struktury.faces
    private const string EpoSandboxUrl =
        "https://adisepo.mfcr.cz/adis/jepo/epo/ePodani/podani.faces";

    // Env variable gate — test is skipped unless set to "true".
    private const string SandboxEnvVar = "RUN_EPO_SANDBOX_TESTS";

    // HTTP timeout for sandbox calls — sandbox may be slow in business hours.
    private static readonly TimeSpan SandboxTimeout = TimeSpan.FromSeconds(30);

    // Synthetic company used for smoke test data (fictitious, never a real taxpayer).
    private const long SmokeCompanyId = 9999L;
    private const string SmokeDic      = "CZ12345678"; // test issuer VAT number
    private const string SmokeClientDic = "CZ87654321"; // test client VAT number

    private readonly FakvioFactory _factory;

    public EpoSandboxSmokeTests(FakvioFactory factory)
    {
        _factory = factory;
        _factory.InitializeDatabase();
        SeedSmokeTestData();
    }

    // =========================================================================
    // Test data seeding
    // =========================================================================

    /// <summary>
    /// Seeds the minimum data required by VatReportService to produce a non-empty
    /// DPHDP3 and DPHKH1 for a single monthly period:
    ///
    ///   - SmokeCompanyId (CZ12345678): issuer, VAT payer, with all EPO header fields.
    ///   - One client (CZ87654321): the invoice recipient.
    ///   - One issued invoice in 2026-M01, total 12 100 CZK incl. 21 % VAT.
    ///     Base = 10 000 CZK, VAT = 2 100 CZK, total w/ VAT = 12 100 CZK.
    ///     This crosses the A.4 threshold (≥ 10 000 CZK incl. VAT) AND has a CZ DIČ.
    ///
    /// Using period 2026/M01 so the test is not sensitive to the current date.
    /// All seeding is idempotent — running the factory constructor multiple times is safe.
    /// </summary>
    private void SeedSmokeTestData()
    {
        using var scope = _factory.Services.CreateScope();
        var masterDb = scope.ServiceProvider.GetRequiredService<MasterDbContext>();
        var tenantDb = scope.ServiceProvider.GetRequiredService<TenantDbContext>();

        // ── Master DB: issuer (Client) + EPO settings ──────────────────────
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
                // EPO header fields required by VatReportService.LoadAndValidateEpoSettingsAsync.
                // Values are from the sandbox acceptance criteria in issue #41.
                EpoTaxOfficeCode        = 451,
                EpoTaxOfficeBranchCode  = 2017,
                EpoContactPhone         = "+420 123 456 789",
                EpoContactEmail         = "test@fakvio.cz",
                EpoAuthorizedPersonName = "Jan Novák"
            });
        }

        masterDb.SaveChanges();

        // ── Tenant DB: issuer + client + invoice ───────────────────────────
        // Issuer must exist in tenant DB as well (VatReportService reads from TenantDbContext).
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

        // The client (invoice recipient) — needs a CZ DIČ to qualify for A.4.
        const long smokeClientId = 9998L;
        if (!tenantDb.Client.Any(c => c.Id == smokeClientId))
        {
            tenantDb.Client.Add(new Client
            {
                Id                 = smokeClientId,
                CompanyName        = "Smoke Test Client a.s.",
                RegistrationNumber = "87654321",
                TaxNumber          = SmokeClientDic,
                IsIssuer           = false,
                IsActive           = true,
                IsVatPayer         = true
            });
            tenantDb.SaveChanges();
        }

        // One issued invoice in period 2026/M01 — provides data for Veta1 (DPHDP3) and A.4 (DPHKH1).
        // TaxableSupplyDate must fall in 2026-01-01 to 2026-01-31 for the smoke period.
        const long smokeInvoiceId = 99001L;
        if (!tenantDb.Invoice.Any(i => i.Id == smokeInvoiceId))
        {
            // CZK currency — seeded by TenantDbContext.SeedData with Code="CZK", Id=1.
            // Fall back to the first available currency if CZK is missing (should not happen).
            var czk = tenantDb.Currency.FirstOrDefault(c => c.Code == "CZK")
                   ?? tenantDb.Currency.First();

            tenantDb.Invoice.Add(new Invoice
            {
                Id                 = smokeInvoiceId,
                DocumentNumber     = "FV-2026-0001",
                DocumentType       = EDocumentType.Invoice,
                Status             = EInvoiceStatus.Completed,
                ClientId           = smokeClientId,
                TaxableSupplyDate  = DateTime.SpecifyKind(new DateTime(2026, 1, 15), DateTimeKind.Utc),
                IssueDate          = DateTime.SpecifyKind(new DateTime(2026, 1, 15), DateTimeKind.Utc),
                DueDate            = DateTime.SpecifyKind(new DateTime(2026, 2, 15), DateTimeKind.Utc),
                TotalBeforeVat     = 10_000m,
                TotalVat           = 2_100m,
                TotalWithVat       = 12_100m,
                CurrencyId         = czk.Id,
                InvoiceItem        =
                [
                    new InvoiceItem
                    {
                        // 10 000 CZK base + 21 % VAT = 2 100 CZK VAT, 12 100 CZK total.
                        // total incl. VAT ≥ 10 000 → qualifies for DPHKH1 A.4 section.
                        Description         = "Smoke test service",
                        Quantity            = 1,
                        UnitPrice           = 10_000m,
                        VatRatePercentage   = 21m,
                        TotalBeforeVat      = 10_000m,
                        VatAmount           = 2_100m,
                        TotalWithVat        = 12_100m
                    }
                ]
            });
            tenantDb.SaveChanges();
        }
    }

    // =========================================================================
    // Smoke tests — DPHDP3
    // =========================================================================

    /// <summary>
    /// Generates a DPHDP3 XML for the smoke test period and POSTs it to the EPO sandbox.
    /// Expected: HTTP 200 and no &lt;chyba&gt; / &lt;error&gt; elements in the response body.
    ///
    /// The test is skipped when:
    ///   - Env variable RUN_EPO_SANDBOX_TESTS is not set to "true".
    ///   - The sandbox responds with 4xx/5xx (portal unavailable).
    ///   - The request times out (network issue).
    /// </summary>
    [SkippableFact]
    public async Task Dphdp3_PostToEpoSandbox_Returns200AndNoValidationErrors()
    {
        // ── Gate: only run when explicitly requested ─────────────────────────
        var runSandbox = Environment.GetEnvironmentVariable(SandboxEnvVar);
        Skip.IfNot(string.Equals(runSandbox, "true", StringComparison.OrdinalIgnoreCase),
            $"Skipped: set {SandboxEnvVar}=true to run EPO sandbox smoke tests.");

        // ── Generate XML via the API endpoint (full stack) ───────────────────
        var xmlBytes = await GenerateEpoXmlViaApiAsync("return");

        // ── POST to EPO sandbox ───────────────────────────────────────────────
        var (statusCode, responseXml) = await PostToEpoSandboxAsync("DPHDP3_smoke.xml", xmlBytes);

        // ── Assert ────────────────────────────────────────────────────────────
        statusCode.ShouldBe(HttpStatusCode.OK,
            $"EPO sandbox returned {(int)statusCode}. Response body:\n{responseXml}");

        AssertNoValidationErrors(responseXml, "DPHDP3");
    }

    // =========================================================================
    // Smoke tests — DPHKH1
    // =========================================================================

    /// <summary>
    /// Generates a DPHKH1 XML for the smoke test period and POSTs it to the EPO sandbox.
    /// Expected: HTTP 200 and no &lt;chyba&gt; / &lt;error&gt; elements in the response body.
    /// </summary>
    [SkippableFact]
    public async Task Dphkh1_PostToEpoSandbox_Returns200AndNoValidationErrors()
    {
        var runSandbox = Environment.GetEnvironmentVariable(SandboxEnvVar);
        Skip.IfNot(string.Equals(runSandbox, "true", StringComparison.OrdinalIgnoreCase),
            $"Skipped: set {SandboxEnvVar}=true to run EPO sandbox smoke tests.");

        var xmlBytes = await GenerateEpoXmlViaApiAsync("control-statement");

        var (statusCode, responseXml) = await PostToEpoSandboxAsync("DPHKH1_smoke.xml", xmlBytes);

        statusCode.ShouldBe(HttpStatusCode.OK,
            $"EPO sandbox returned {(int)statusCode}. Response body:\n{responseXml}");

        AssertNoValidationErrors(responseXml, "DPHKH1");
    }

    // =========================================================================
    // Private helpers
    // =========================================================================

    /// <summary>
    /// Calls the Fakvio API EPO endpoint (via the test factory's HttpClient) as the
    /// smoke test company and returns the raw XML bytes.
    ///
    /// The API is the full stack — VatReportService → XSD validation → serialise.
    /// This verifies both that the XML is generated AND that it passes our own XSD check
    /// before we send it to the external sandbox.
    /// </summary>
    /// <param name="formType">"return" for DPHDP3, "control-statement" for DPHKH1.</param>
    private async Task<byte[]> GenerateEpoXmlViaApiAsync(string formType)
    {
        var client = _factory.CreateClient();
        var loginResponse = await AuthHelper.LoginAsSysAdminAsync(client);
        AuthHelper.SetAuthToken(client, loginResponse.Token);
        AuthHelper.SetImpersonation(client, SmokeCompanyId);

        // Period: 2026/M01 — contains our seeded invoice (TaxableSupplyDate = 2026-01-15).
        var url = $"/api/vat-report/epo/{formType}?year=2026&period=1&type=Monthly";
        var response = await client.GetAsync(url);

        response.StatusCode.ShouldBe(HttpStatusCode.OK,
            $"Fakvio API returned {(int)response.StatusCode} for {url}. " +
            $"Body: {await response.Content.ReadAsStringAsync()}");

        return await response.Content.ReadAsByteArrayAsync();
    }

    /// <summary>
    /// Sends the XML bytes to the EPO sandbox as a multipart/form-data upload.
    ///
    /// EPO expects:
    ///   POST https://adisepo.mfcr.cz/adis/jepo/epo/ePodani/podani.faces
    ///   Content-Type: multipart/form-data
    ///   Field: soubor (the XML file)
    ///
    /// Returns (statusCode, responseBody).
    ///
    /// The method itself never throws — if the sandbox is unreachable or returns
    /// 4xx/5xx, it returns the status code and the caller decides to skip or fail.
    /// </summary>
    private static async Task<(HttpStatusCode StatusCode, string Body)> PostToEpoSandboxAsync(
        string filename,
        byte[] xmlBytes)
    {
        using var cts = new CancellationTokenSource(SandboxTimeout);

        // EPO anonymous upload endpoint uses a plain multipart form with field "soubor".
        using var content = new MultipartFormDataContent();
        var fileContent = new ByteArrayContent(xmlBytes);
        fileContent.Headers.ContentType =
            new System.Net.Http.Headers.MediaTypeHeaderValue("application/xml");
        content.Add(fileContent, "soubor", filename);

        using var http = new HttpClient();

        try
        {
            var response = await http.PostAsync(EpoSandboxUrl, content, cts.Token);
            var body = await response.Content.ReadAsStringAsync(cts.Token);
            return (response.StatusCode, body);
        }
        catch (OperationCanceledException)
        {
            // Timeout — skip rather than fail.
            Skip.If(true,
                $"EPO sandbox timed out after {SandboxTimeout.TotalSeconds}s. " +
                "Sandbox may be unavailable. Re-run later with RUN_EPO_SANDBOX_TESTS=true.");
            return (0, string.Empty); // unreachable — Skip.If throws SkipException
        }
        catch (HttpRequestException ex)
        {
            // Network error — skip rather than fail.
            Skip.If(true,
                $"EPO sandbox unreachable: {ex.Message}. " +
                "Check network connectivity and re-run with RUN_EPO_SANDBOX_TESTS=true.");
            return (0, string.Empty); // unreachable
        }
    }

    /// <summary>
    /// Asserts that the EPO sandbox response XML does not contain validation error elements.
    ///
    /// EPO returns errors as XML elements named "chyba" (Czech) or "error" (English alias).
    /// A response without these elements (and with HTTP 200) indicates the document was accepted.
    ///
    /// If parsing fails (non-XML response), the test skips — the sandbox may have returned
    /// an HTML error page, which is a server-side issue, not a bug in our generator.
    /// </summary>
    private static void AssertNoValidationErrors(string responseBody, string formName)
    {
        if (string.IsNullOrWhiteSpace(responseBody))
            return; // empty body with HTTP 200 is acceptable (some sandbox implementations)

        XDocument? doc;
        try
        {
            doc = XDocument.Parse(responseBody);
        }
        catch (System.Xml.XmlException)
        {
            // Non-XML response (HTML error page etc.) — skip the content check.
            // HTTP 200 was already asserted; non-XML body means sandbox returned status page.
            Skip.If(true,
                $"{formName}: EPO sandbox returned non-XML body (HTTP 200). " +
                "Cannot check for validation errors. Response:\n" +
                responseBody[..Math.Min(500, responseBody.Length)]);
            return; // unreachable
        }

        // Look for any <chyba> or <error> element anywhere in the response tree.
        var errorElements = doc.Descendants()
            .Where(e =>
                string.Equals(e.Name.LocalName, "chyba",  StringComparison.OrdinalIgnoreCase) ||
                string.Equals(e.Name.LocalName, "error",  StringComparison.OrdinalIgnoreCase))
            .ToList();

        errorElements.ShouldBeEmpty(
            $"{formName}: EPO sandbox returned validation error(s):\n" +
            string.Join("\n", errorElements.Select(e => e.ToString())));
    }
}

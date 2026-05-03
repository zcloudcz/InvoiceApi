using System.Net;
using System.Text.Json;
using Fakvio.Domain.Entities;
using Fakvio.Infrastructure.Data;
using Fakvio.Tests.Integration.Fixtures;
using Fakvio.Tests.Integration.Helpers;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;

namespace Fakvio.Tests.Integration;

/// <summary>
/// Integration tests that verify the HTTP 403 VAT_PAYER_REQUIRED path for both
/// EPO export endpoints.
///
/// Isolated in a separate test class (own FakvioFactory) because the shared
/// TenantDbContext in EpoApiEndpointTests can only hold one "first active issuer"
/// — these tests require that issuer to have IsVatPayer = false, which conflicts
/// with the happy-path tests that require IsVatPayer = true.
///
/// Each class that implements IClassFixture&lt;FakvioFactory&gt; gets its own
/// factory instance with its own InMemoryDatabase, so the issuer seeded here
/// does not pollute other test classes.
/// </summary>
public class EpoNonVatPayerTests : IClassFixture<FakvioFactory>
{
    private readonly FakvioFactory _factory;

    // A single company ID — the only issuer in this factory's tenant DB.
    private const long CompanyId = 45L;

    public EpoNonVatPayerTests(FakvioFactory factory)
    {
        _factory = factory;
        _factory.InitializeDatabase();
        SeedNonVatPayerScenario();
    }

    // =========================================================================
    // Seed helper
    // =========================================================================

    /// <summary>
    /// Seeds a company (master DB) and its issuer (tenant DB) with IsVatPayer = false.
    /// EPO settings are provided so the check does not fail on EPO_HEADER_INCOMPLETE first.
    /// </summary>
    private void SeedNonVatPayerScenario()
    {
        using var scope = _factory.Services.CreateScope();
        var masterDb = scope.ServiceProvider.GetRequiredService<MasterDbContext>();
        var tenantDb = scope.ServiceProvider.GetRequiredService<TenantDbContext>();

        // Master DB: company record + CompanySystemSettings with EPO fields set.
        if (!masterDb.Client.Any(c => c.Id == CompanyId))
        {
            masterDb.Client.Add(new Client
            {
                Id = CompanyId,
                CompanyName = "Non-VAT Company",
                RegistrationNumber = "N0000045",
                TaxNumber = null, // non-payer has no DIČ
                IsIssuer = true,
                IsActive = true
            });
        }

        if (!masterDb.CompanySystemSettings.Any(s => s.CompanyId == CompanyId))
        {
            masterDb.CompanySystemSettings.Add(new CompanySystemSettings
            {
                CompanyId = CompanyId,
                SchemaName = "tenant_45",
                IsProvisioned = true,
                IsActive = true,
                // Required EPO header fields — present so the service reaches the
                // IsVatPayer check before failing on EPO_HEADER_INCOMPLETE.
                EpoTaxOfficeCode = 451,
                EpoTaxOfficeBranchCode = 2017
            });
        }

        masterDb.SaveChanges();

        // Tenant DB: issuer with IsVatPayer = false — the flag under test.
        // This is the ONLY issuer in this factory's tenant InMemoryDatabase.
        if (!tenantDb.Client.Any(c => c.IsIssuer && c.Id == CompanyId))
        {
            tenantDb.Client.Add(new Client
            {
                Id = CompanyId,
                CompanyName = "Non-VAT Company",
                RegistrationNumber = "N0000045",
                TaxNumber = null,
                IsIssuer = true,
                IsActive = true,
                IsVatPayer = false // the flag that triggers VAT_PAYER_REQUIRED
            });
            tenantDb.SaveChanges();
        }
    }

    // =========================================================================
    // Non-VAT payer → 403 VAT_PAYER_REQUIRED
    // =========================================================================

    [Fact]
    public async Task EpoReturn_NonVatPayer_Returns403WithCode_VatPayerRequired()
    {
        var client = _factory.CreateClient();
        var loginResponse = await AuthHelper.LoginAsSysAdminAsync(client);
        AuthHelper.SetAuthToken(client, loginResponse.Token);
        AuthHelper.SetImpersonation(client, CompanyId);

        var response = await client.GetAsync("/api/vat-report/epo/return?year=2026&period=3&type=Monthly");

        response.StatusCode.ShouldBe(HttpStatusCode.Forbidden);

        var body = await response.Content.ReadAsStringAsync();
        using var doc = JsonDocument.Parse(body);
        doc.RootElement.GetProperty("code").GetString()
            .ShouldBe("VAT_PAYER_REQUIRED",
                "Response body must contain machine-readable code VAT_PAYER_REQUIRED.");
    }

    [Fact]
    public async Task EpoControlStatement_NonVatPayer_Returns403WithCode_VatPayerRequired()
    {
        var client = _factory.CreateClient();
        var loginResponse = await AuthHelper.LoginAsSysAdminAsync(client);
        AuthHelper.SetAuthToken(client, loginResponse.Token);
        AuthHelper.SetImpersonation(client, CompanyId);

        var response = await client.GetAsync("/api/vat-report/epo/control-statement?year=2026&period=3&type=Monthly");

        response.StatusCode.ShouldBe(HttpStatusCode.Forbidden);

        var body = await response.Content.ReadAsStringAsync();
        using var doc = JsonDocument.Parse(body);
        doc.RootElement.GetProperty("code").GetString()
            .ShouldBe("VAT_PAYER_REQUIRED",
                "Response body must contain machine-readable code VAT_PAYER_REQUIRED.");
    }
}

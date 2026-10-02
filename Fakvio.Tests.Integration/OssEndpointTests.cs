using System.Net;
using System.Net.Http.Json;
using Fakvio.Contracts.Dto.OssReport;
using Fakvio.Contracts.Dto.OssVatRate;
using Fakvio.Domain.Entities;
using Fakvio.Domain.Enums;
using Fakvio.Infrastructure.Data;
using Fakvio.Tests.Integration.Fixtures;
using Fakvio.Tests.Integration.Helpers;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;

namespace Fakvio.Tests.Integration;

/// <summary>
/// EU OSS endpoints (DEVGUIDE §4.15): /api/oss-report (tenant-scoped) and /api/oss-vat-rate (master-only code table).
/// </summary>
public class OssEndpointTests : IClassFixture<FakvioFactory>
{
    private const long CompanyId = 52L;
    private readonly FakvioFactory _factory;

    public OssEndpointTests(FakvioFactory factory)
    {
        _factory = factory;
        _factory.InitializeDatabase();
    }

    private void SeedTenant()
    {
        using var scope = _factory.Services.CreateScope();
        var master = scope.ServiceProvider.GetRequiredService<MasterDbContext>();
        var tenant = scope.ServiceProvider.GetRequiredService<TenantDbContext>();
        if (!master.Client.Any(c => c.Id == CompanyId))
            master.Client.Add(new Client { Id = CompanyId, CompanyName = "Oss Co", RegistrationNumber = "R52", IsIssuer = true, IsActive = true });
        if (!master.CompanySystemSettings.Any(s => s.CompanyId == CompanyId))
            master.CompanySystemSettings.Add(new CompanySystemSettings { CompanyId = CompanyId, SchemaName = "t52", IsProvisioned = true, IsActive = true });
        master.SaveChanges();
        if (!tenant.Client.Any(c => c.IsIssuer))
        {
            tenant.Client.Add(new Client { Id = CompanyId, CompanyName = "Oss Co", RegistrationNumber = "R52", IsIssuer = true, IsActive = true, IsVatPayer = true });
            tenant.SaveChanges();
        }
    }

    private async Task<HttpClient> SysAdminAsync(bool impersonate)
    {
        var client = _factory.CreateClient();
        var login = await AuthHelper.LoginAsSysAdminAsync(client);
        AuthHelper.SetAuthToken(client, login.Token);
        if (impersonate) AuthHelper.SetImpersonation(client, CompanyId);
        return client;
    }

    [Fact]
    public async Task Report_Unauthenticated_Returns401()
    {
        var response = await _factory.CreateClient().GetAsync("/api/oss-report?year=2026&quarter=1");
        response.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
    }

    [Theory]
    [InlineData("/api/oss-report?year=2026&quarter=5")]
    [InlineData("/api/oss-report/csv?year=2019&quarter=1")]
    public async Task Report_InvalidPeriod_Returns400(string url)
    {
        SeedTenant();
        var client = await SysAdminAsync(impersonate: true);

        (await client.GetAsync(url)).StatusCode.ShouldBe(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task Report_NoOssInvoices_ReturnsEmptyReport_AndCsv()
    {
        SeedTenant();
        var client = await SysAdminAsync(impersonate: true);

        var report = await client.GetFromJsonAsync<OssReportDto>("/api/oss-report?year=2026&quarter=2");
        report!.Lines.ShouldBeEmpty();
        report.Quarter.ShouldBe(2);

        var csv = await client.GetAsync("/api/oss-report/csv?year=2026&quarter=2");
        csv.StatusCode.ShouldBe(HttpStatusCode.OK);
        csv.Content.Headers.ContentDisposition!.FileName.ShouldBe("OSS_2026_Q2.csv");
    }

    [Fact]
    public async Task Rates_MasterOnlyPath_WorksWithoutImpersonation_AndValidatesCountry()
    {
        var client = await SysAdminAsync(impersonate: false);

        (await client.GetAsync("/api/oss-vat-rate/country/US")).StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await client.GetAsync("/api/oss-vat-rate/country/CZ")).StatusCode.ShouldBe(HttpStatusCode.BadRequest);

        var create = await client.PostAsJsonAsync("/api/oss-vat-rate", new CreateOssVatRateDto
        {
            CountryCode = "de", Rate = 19, Category = EOssVatRateCategory.Standard, ValidFrom = new DateOnly(2021, 7, 1)
        });
        create.StatusCode.ShouldBe(HttpStatusCode.Created);

        var rates = await client.GetFromJsonAsync<List<OssVatRateDto>>("/api/oss-vat-rate/country/DE");
        rates!.ShouldContain(r => r.Rate == 19m && r.CountryCode == "DE");

        (await client.PostAsJsonAsync("/api/oss-vat-rate", new CreateOssVatRateDto { CountryCode = "US", Rate = 5, ValidFrom = new DateOnly(2021, 7, 1) }))
            .StatusCode.ShouldBe(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task Rates_Unauthenticated_Returns401()
        => (await _factory.CreateClient().GetAsync("/api/oss-vat-rate/country/DE")).StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
}

using System.IdentityModel.Tokens.Jwt;
using System.Net;
using System.Security.Claims;
using System.Text;
using Microsoft.IdentityModel.Tokens;
using Fakvio.Contracts.Dto.CompanySettings;
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
/// EU OSS endpoints (DEVGUIDE §4.16): /api/oss-report (tenant-scoped) and /api/oss-vat-rate (master-only code table).
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
        if (!master.User.Any(u => u.Id == 5201))
            master.User.Add(new User { Id = 5201, CompanyId = CompanyId, Email = "oss-admin@example.test", Role = EUserRole.Admin });
        if (!master.User.Any(u => u.Id == 5202))
            master.User.Add(new User { Id = 5202, CompanyId = CompanyId, Email = "oss-user@example.test", Role = EUserRole.User });
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

    private HttpClient Jwt(string role, long userId = 5201)
    {
        var claims = new List<Claim> { new(ClaimTypes.Role, role), new(ClaimTypes.NameIdentifier, userId.ToString()), new("CompanyId", CompanyId.ToString()) };
        var token = new JwtSecurityToken("Fakvio.Tests", "Fakvio.Tests.Client", claims, expires: DateTime.UtcNow.AddMinutes(5),
            signingCredentials: new SigningCredentials(new SymmetricSecurityKey(
                Encoding.UTF8.GetBytes("TestSecretKeyForIntegrationTestsThatMustBeAtLeast32BytesLong!")), SecurityAlgorithms.HmacSha256));
        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new("Bearer", new JwtSecurityTokenHandler().WriteToken(token));
        return client;
    }

    [Fact]
    public async Task TenantAdmin_SavesOss_Returns200_AndOtherSettingsUntouched()
    {
        SeedTenant();
        using (var scope = _factory.Services.CreateScope())
        {
            var master = scope.ServiceProvider.GetRequiredService<MasterDbContext>();
            var settings = master.CompanySystemSettings.Single(x => x.CompanyId == CompanyId);
            settings.MaxUsers = 7;
            settings.AdminNotes = "keep me";
            master.SaveChanges();
        }

        using var admin = Jwt("Admin");
        var put = await admin.PutAsJsonAsync("/api/company-settings/oss",
            new OssSettingsDto { OssRegistered = true, OssRegisteredSince = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Unspecified) });
        put.StatusCode.ShouldBe(HttpStatusCode.OK, await put.Content.ReadAsStringAsync());

        var read = await admin.GetFromJsonAsync<OssSettingsDto>("/api/company-settings/oss");
        read!.OssRegistered.ShouldBeTrue();
        read.OssRegisteredSince!.Value.Date.ShouldBe(new DateTime(2026, 1, 1));

        using var verify = _factory.Services.CreateScope();
        var saved = verify.ServiceProvider.GetRequiredService<MasterDbContext>().CompanySystemSettings.Single(x => x.CompanyId == CompanyId);
        saved.MaxUsers.ShouldBe(7);
        saved.AdminNotes.ShouldBe("keep me");
        saved.OssRegisteredSince!.Value.Kind.ShouldBe(DateTimeKind.Utc);

        // switching off clears the date
        (await admin.PutAsJsonAsync("/api/company-settings/oss", new OssSettingsDto { OssRegistered = false })).StatusCode.ShouldBe(HttpStatusCode.OK);
        (await admin.GetFromJsonAsync<OssSettingsDto>("/api/company-settings/oss"))!.OssRegisteredSince.ShouldBeNull();
    }

    [Fact]
    public async Task OrdinaryUser_CannotSaveOss_Returns403()
    {
        SeedTenant();
        using var user = Jwt("User", 5202);
        (await user.PutAsJsonAsync("/api/company-settings/oss", new OssSettingsDto { OssRegistered = true })).StatusCode.ShouldBe(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task OssSettings_Unauthenticated_Returns401()
        => (await _factory.CreateClient().PutAsJsonAsync("/api/company-settings/oss", new OssSettingsDto())).StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
}

using System.Net;
using System.Net.Http.Json;
using Fakvio.Contracts.Dto.Dashboard;
using Fakvio.Domain.Entities;
using Fakvio.Infrastructure.Data;
using Fakvio.Tests.Integration.Fixtures;
using Fakvio.Tests.Integration.Helpers;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;

namespace Fakvio.Tests.Integration;

/// <summary>
/// Endpoint tests for <c>GET /api/dashboard</c> — the new widget series
/// (revenue by month, income vs. expense, receivables aging) must always be present.
/// </summary>
public class DashboardEndpointTests : IClassFixture<FakvioFactory>
{
    private readonly FakvioFactory _factory;

    // Unique company id (other integration tests use 42, 43, 501, 601).
    private const long TestCompanyId = 701L;

    public DashboardEndpointTests(FakvioFactory factory)
    {
        _factory = factory;
        _factory.InitializeDatabase();
    }

    [Fact]
    public async Task Get_WithoutAuth_Returns401()
    {
        var response = await _factory.CreateClient().GetAsync("/api/dashboard");

        response.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task Get_EmptyTenant_ReturnsTwelveMonthSeriesAndEmptyAging()
    {
        SeedCompany();
        var client = _factory.CreateClient();
        var login = await AuthHelper.LoginAsSysAdminAsync(client);
        AuthHelper.SetAuthToken(client, login.Token);
        AuthHelper.SetImpersonation(client, TestCompanyId);

        var response = await client.GetAsync("/api/dashboard");

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        var dto = await response.Content.ReadFromJsonAsync<DashboardDto>();
        dto.ShouldNotBeNull();
        dto!.RevenueByMonth.Count.ShouldBe(12);
        dto.IncomeVsExpenseByMonth.Count.ShouldBe(12);
        dto.RevenueByMonth.ShouldAllBe(m => m.Amount == 0m);
        dto.ReceivablesAging.Bucket0To30.ShouldBe(0m);
        dto.ReceivablesAging.BucketOver90.ShouldBe(0m);
    }

    /// <summary>Seeds the master rows impersonation needs (company + provisioned settings).</summary>
    private void SeedCompany()
    {
        using var scope = _factory.Services.CreateScope();
        var masterDb = scope.ServiceProvider.GetRequiredService<MasterDbContext>();

        if (!masterDb.Client.Any(c => c.Id == TestCompanyId))
        {
            masterDb.Client.Add(new Client
            {
                Id = TestCompanyId,
                CompanyName = "Dashboard Test Company",
                RegistrationNumber = "T0000701",
                TaxNumber = "CZ00000701",
                IsIssuer = true,
                IsActive = true
            });
        }

        if (!masterDb.CompanySystemSettings.Any(s => s.CompanyId == TestCompanyId))
        {
            masterDb.CompanySystemSettings.Add(new CompanySystemSettings
            {
                CompanyId = TestCompanyId,
                SchemaName = $"tenant_{TestCompanyId}",
                IsProvisioned = true,
                IsActive = true
            });
        }

        masterDb.SaveChanges();
    }
}

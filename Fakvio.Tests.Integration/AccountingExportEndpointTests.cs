using System.Net;
using System.Net.Http.Json;
using System.Xml.Linq;
using Fakvio.Contracts.Dto.AccountingExport;
using Fakvio.Domain.Entities;
using Fakvio.Domain.Enums;
using Fakvio.Infrastructure.Data;
using Fakvio.Tests.Integration.Fixtures;
using Fakvio.Tests.Integration.Helpers;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;

namespace Fakvio.Tests.Integration;

/// <summary>
/// Integration tests for POST /api/accounting-export/{system} ("Export do účetnictví"):
/// authentication, request validation and a happy path that returns a downloadable XML file
/// containing a seeded invoice, for each of the three supported systems.
/// </summary>
public class AccountingExportEndpointTests : IClassFixture<FakvioFactory>
{
    private const long TestCompanyId = 61L;
    private readonly FakvioFactory _factory;

    public AccountingExportEndpointTests(FakvioFactory factory)
    {
        _factory = factory;
        _factory.InitializeDatabase();
    }

    /// <summary>Seeds the company in the master DB plus an issuer and one completed invoice in the tenant DB.</summary>
    private void SeedCompanyWithInvoice()
    {
        using var scope = _factory.Services.CreateScope();
        var masterDb = scope.ServiceProvider.GetRequiredService<MasterDbContext>();
        var tenantDb = scope.ServiceProvider.GetRequiredService<TenantDbContext>();

        if (!masterDb.Client.Any(c => c.Id == TestCompanyId))
        {
            masterDb.Client.Add(new Client
            {
                Id = TestCompanyId, CompanyName = "Export Test s.r.o.", RegistrationNumber = "T0000061",
                TaxNumber = "CZ00000061", IsIssuer = true, IsActive = true
            });
            masterDb.CompanySystemSettings.Add(new CompanySystemSettings
            {
                CompanyId = TestCompanyId, SchemaName = $"tenant_{TestCompanyId}", IsProvisioned = true, IsActive = true
            });
            masterDb.SaveChanges();
        }

        if (tenantDb.Invoice.Any(i => i.DocumentNumber == "EXPORT-1")) return;

        var issuer = tenantDb.Client.FirstOrDefault(c => c.IsIssuer) ?? new Client
        {
            CompanyName = "Export Test s.r.o.", RegistrationNumber = "T0000061", TaxNumber = "CZ00000061",
            IsIssuer = true, IsActive = true, IsVatPayer = true
        };
        var customer = new Client { CompanyName = "Customer a.s.", RegistrationNumber = "27082440", IsActive = true };
        var currency = tenantDb.Currency.FirstOrDefault(c => c.Code == "CZK") ?? new Currency { Code = "CZK", Name = "Koruna", Symbol = "Kč" };

        tenantDb.Invoice.Add(new Invoice
        {
            DocumentNumber = "EXPORT-1", VariableSymbol = "2026001", DocumentType = EDocumentType.Invoice,
            Status = EInvoiceStatus.Completed, IssueDate = new DateTime(2026, 3, 10), DueDate = new DateTime(2026, 3, 24),
            Issuer = issuer, Client = customer, Currency = currency,
            TotalBeforeVat = 100m, TotalVat = 21m, TotalWithVat = 121m,
            InvoiceItem =
            [
                new InvoiceItem
                {
                    OrderIndex = 1, Description = "Consulting", Quantity = 1, Unit = "ks", UnitPrice = 100m,
                    VatRatePercentage = 21m, TotalBeforeVat = 100m, VatAmount = 21m, TotalWithVat = 121m
                }
            ]
        });
        tenantDb.SaveChanges();
    }

    private async Task<HttpClient> LoginAsync()
    {
        SeedCompanyWithInvoice();
        var client = _factory.CreateClient();
        var login = await AuthHelper.LoginAsSysAdminAsync(client);
        AuthHelper.SetAuthToken(client, login.Token);
        AuthHelper.SetImpersonation(client, TestCompanyId);
        return client;
    }

    [Fact]
    public async Task Export_Unauthenticated_Returns401()
    {
        var response = await _factory.CreateClient().PostAsJsonAsync("/api/accounting-export/Pohoda",
            new AccountingExportRequestDto { From = new DateTime(2026, 3, 1), To = new DateTime(2026, 3, 31) });

        response.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task Export_NeitherIssuedNorReceived_Returns400()
    {
        var client = await LoginAsync();

        var response = await client.PostAsJsonAsync("/api/accounting-export/Pohoda", new AccountingExportRequestDto
        {
            From = new DateTime(2026, 3, 1), To = new DateTime(2026, 3, 31), IncludeIssued = false, IncludeReceived = false
        });

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
    }

    [Theory]
    [InlineData("Pohoda")]
    [InlineData("MoneyS3")]
    [InlineData("AbraFlexi")]
    public async Task Export_IssuedInvoiceInRange_ReturnsXmlFileContainingInvoice(string system)
    {
        var client = await LoginAsync();

        var response = await client.PostAsJsonAsync($"/api/accounting-export/{system}", new AccountingExportRequestDto
        {
            From = new DateTime(2026, 3, 1), To = new DateTime(2026, 3, 31), IncludeIssued = true, IncludeReceived = true
        });

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        response.Content.Headers.ContentType!.MediaType.ShouldBe("application/xml");
        response.Content.Headers.ContentDisposition!.FileNameStar.ShouldBe($"{system}_20260301-20260331.xml");
        var xml = XDocument.Load(await response.Content.ReadAsStreamAsync());
        xml.ToString().ShouldContain("EXPORT-1");
    }

    [Fact]
    public async Task Export_InvertedOrTooLongRange_Returns400()
    {
        var client = await LoginAsync();

        var inverted = await client.PostAsJsonAsync("/api/accounting-export/Pohoda", new AccountingExportRequestDto
        {
            From = new DateTime(2026, 3, 31), To = new DateTime(2026, 3, 1)
        });
        var tooLong = await client.PostAsJsonAsync("/api/accounting-export/Pohoda", new AccountingExportRequestDto
        {
            From = new DateTime(2024, 1, 1), To = new DateTime(2026, 3, 1)
        });

        inverted.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        tooLong.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task Export_ReportsExportedAndSkippedCountsInHeaders()
    {
        var client = await LoginAsync();

        var response = await client.PostAsJsonAsync("/api/accounting-export/MoneyS3", new AccountingExportRequestDto
        {
            From = new DateTime(2026, 3, 1), To = new DateTime(2026, 3, 31)
        });

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        response.Headers.GetValues("X-Export-Exported").Single().ShouldBe("1");
        response.Headers.GetValues("X-Export-Skipped").Single().ShouldBe("0");
    }

    [Fact]
    public async Task Export_OutsideDateRange_ReturnsFileWithoutInvoice()
    {
        var client = await LoginAsync();

        var response = await client.PostAsJsonAsync("/api/accounting-export/AbraFlexi", new AccountingExportRequestDto
        {
            From = new DateTime(2025, 1, 1), To = new DateTime(2025, 1, 31)
        });

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        XDocument.Load(await response.Content.ReadAsStreamAsync()).ToString().ShouldNotContain("EXPORT-1");
    }
}

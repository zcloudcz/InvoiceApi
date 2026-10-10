using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Fakvio.Application.Service;
using Fakvio.Contracts.Dto.DownloadLink;
using Fakvio.Domain.Entities;
using Fakvio.Domain.Enums;
using Fakvio.Infrastructure.Data;
using Fakvio.Tests.Integration.Fixtures;
using Fakvio.Tests.Integration.Helpers;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;

namespace Fakvio.Tests.Integration;

/// <summary>
/// Integration tests for the signed download links: POST /api/download-links (authenticated, mints a link)
/// and GET /api/download/{token} (anonymous, token is the credential). Uses the accounting export as the
/// downloadable file because it is easy to seed; the dispatch is the same for every kind.
/// </summary>
public class DownloadLinkEndpointTests : IClassFixture<FakvioFactory>
{
    private const long TestCompanyId = 62L;
    private readonly FakvioFactory _factory;

    public DownloadLinkEndpointTests(FakvioFactory factory)
    {
        _factory = factory;
        _factory.InitializeDatabase();
    }

    private void SeedCompanyWithInvoice(FakvioFactory factory)
    {
        using var scope = factory.Services.CreateScope();
        var masterDb = scope.ServiceProvider.GetRequiredService<MasterDbContext>();
        var tenantDb = scope.ServiceProvider.GetRequiredService<TenantDbContext>();

        if (!masterDb.Client.Any(c => c.Id == TestCompanyId))
        {
            masterDb.Client.Add(new Client
            {
                Id = TestCompanyId, CompanyName = "Link Test s.r.o.", RegistrationNumber = "T0000062",
                TaxNumber = "CZ00000062", IsIssuer = true, IsActive = true
            });
            masterDb.CompanySystemSettings.Add(new CompanySystemSettings
            {
                CompanyId = TestCompanyId, SchemaName = $"tenant_{TestCompanyId}", IsProvisioned = true, IsActive = true
            });
            masterDb.SaveChanges();
        }

        if (tenantDb.Invoice.Any(i => i.DocumentNumber == "LINK-1")) return;

        var issuer = tenantDb.Client.FirstOrDefault(c => c.IsIssuer) ?? new Client
        {
            CompanyName = "Link Test s.r.o.", RegistrationNumber = "T0000062", TaxNumber = "CZ00000062",
            IsIssuer = true, IsActive = true, IsVatPayer = true
        };
        var customer = new Client { CompanyName = "Customer a.s.", RegistrationNumber = "27082440", IsActive = true };
        var currency = tenantDb.Currency.FirstOrDefault(c => c.Code == "CZK") ?? new Currency { Code = "CZK", Name = "Koruna", Symbol = "Kč" };

        tenantDb.Invoice.Add(new Invoice
        {
            DocumentNumber = "LINK-1", VariableSymbol = "2026062", DocumentType = EDocumentType.Invoice,
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

    private static CreateDownloadLinkDto AccountingRequest() => new()
    {
        Kind = EDownloadKind.AccountingExport,
        AccountingSystem = EAccountingSystem.Pohoda,
        From = new DateTime(2026, 3, 1),
        To = new DateTime(2026, 3, 31)
    };

    /// <summary>Authenticated client (SysAdmin impersonating the test company) - used ONLY to mint links.</summary>
    private static async Task<HttpClient> LoginAsync(Microsoft.AspNetCore.Mvc.Testing.WebApplicationFactory<Program> factory)
    {
        var client = factory.CreateClient();
        var login = await AuthHelper.LoginAsSysAdminAsync(client);
        AuthHelper.SetAuthToken(client, login.Token);
        AuthHelper.SetImpersonation(client, TestCompanyId);
        return client;
    }

    private static async Task<string> MintTokenAsync(HttpClient authed, CreateDownloadLinkDto request)
    {
        var response = await authed.PostAsJsonAsync("/api/download-links", request);
        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        var link = (await response.Content.ReadFromJsonAsync<DownloadLinkDto>())!;
        return link.Url[(link.Url.LastIndexOf('/') + 1)..];
    }

    [Fact]
    public async Task CreateLink_Unauthenticated_Returns401()
    {
        var response = await _factory.CreateClient().PostAsJsonAsync("/api/download-links", AccountingRequest());

        response.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task CreateLink_InvalidParameters_Returns400()
    {
        SeedCompanyWithInvoice(_factory);
        var authed = await LoginAsync(_factory);

        var response = await authed.PostAsJsonAsync("/api/download-links",
            new CreateDownloadLinkDto { Kind = EDownloadKind.InvoiceBulkPdf, Ids = [] });

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task Download_ValidLink_WorksWithoutAuthorizationHeader_AndIsNotCacheable()
    {
        SeedCompanyWithInvoice(_factory);
        var authed = await LoginAsync(_factory);
        var response = await authed.PostAsJsonAsync("/api/download-links", AccountingRequest());
        var link = (await response.Content.ReadFromJsonAsync<DownloadLinkDto>())!;
        link.ExpiresAt.ShouldBeGreaterThan(DateTime.UtcNow);

        // A brand-new client: no Authorization header, no impersonation header - the token is the only credential.
        var anonymous = _factory.CreateClient();
        var file = await anonymous.GetAsync(new Uri(link.Url).PathAndQuery);

        file.StatusCode.ShouldBe(HttpStatusCode.OK);
        file.Content.Headers.ContentType!.MediaType.ShouldBe("application/xml");
        file.Content.Headers.ContentDisposition!.DispositionType.ShouldBe("attachment");
        file.Headers.CacheControl!.NoStore.ShouldBeTrue();
        (await file.Content.ReadAsStringAsync()).ShouldContain("LINK-1");
    }

    [Fact]
    public async Task Download_ExpiredLink_Returns404()
    {
        // 0.0001 minutes = 6 ms lifetime: the token is dead by the time it is redeemed.
        using var shortLived = _factory.WithWebHostBuilder(b => b.UseSetting("DownloadLinks:LifetimeMinutes", "0.0001"));
        SeedCompanyWithInvoice(_factory);
        var authed = await LoginAsync(shortLived);
        var token = await MintTokenAsync(authed, AccountingRequest());

        await Task.Delay(300);
        var response = await shortLived.CreateClient().GetAsync($"/api/download/{token}");

        response.StatusCode.ShouldBe(HttpStatusCode.NotFound);
        (await response.Content.ReadAsStringAsync()).ShouldNotContain(token); // generic ProblemDetails only, nothing about the token
    }

    [Fact]
    public async Task Download_TamperedToken_Returns404()
    {
        SeedCompanyWithInvoice(_factory);
        var token = await MintTokenAsync(await LoginAsync(_factory), AccountingRequest());

        var i = token.Length / 2;
        var tampered = token[..i] + (token[i] == 'A' ? 'B' : 'A') + token[(i + 1)..];
        var response = await _factory.CreateClient().GetAsync($"/api/download/{tampered}");

        response.StatusCode.ShouldBe(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task Download_GarbageToken_Returns404()
    {
        var response = await _factory.CreateClient().GetAsync("/api/download/not-a-token");

        response.StatusCode.ShouldBe(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task Download_TokenOfAnotherPurpose_Returns404()
    {
        // A valid, unexpired token from the same key ring, but protected for a different feature.
        var provider = _factory.Services.GetRequiredService<IDataProtectionProvider>();
        var foreign = provider.CreateProtector("Fakvio.OAuth.AuthorizeTicket.v1").ToTimeLimitedDataProtector()
            .Protect(JsonSerializer.Serialize(new DownloadTokenPayload(TestCompanyId, 1, AccountingRequest())), TimeSpan.FromMinutes(5));

        var response = await _factory.CreateClient().GetAsync($"/api/download/{foreign}");

        response.StatusCode.ShouldBe(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task Download_TokenForUnknownTenant_Returns404()
    {
        // The tenant is taken from the token only. A company that is not provisioned (or not active) is not
        // reachable through ANY token, so a link can never be pointed at a tenant it was not minted for.
        var protector = _factory.Services.GetRequiredService<IDownloadLinkProtector>();
        var token = protector.Protect(new DownloadTokenPayload(987654, 1, AccountingRequest()), out _);

        var response = await _factory.CreateClient().GetAsync($"/api/download/{token}");

        response.StatusCode.ShouldBe(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task Download_TokenWithInvalidParameters_Returns404()
    {
        // Even a correctly signed token is re-validated: kind needs an Id and none is present.
        SeedCompanyWithInvoice(_factory);
        var protector = _factory.Services.GetRequiredService<IDownloadLinkProtector>();
        var token = protector.Protect(
            new DownloadTokenPayload(TestCompanyId, 1, new CreateDownloadLinkDto { Kind = EDownloadKind.InvoicePdf }), out _);

        var response = await _factory.CreateClient().GetAsync($"/api/download/{token}");

        response.StatusCode.ShouldBe(HttpStatusCode.NotFound);
    }
}

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
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Shouldly;

namespace Fakvio.Tests.Integration;

/// <summary>
/// Integration tests for the signed download links: POST /api/download-links (authenticated, mints a link)
/// and GET /api/download/{token} (anonymous, token is the credential). Most tests use the accounting export
/// as the downloadable file because it is easy to seed; a single-document and a bulk kind are redeemed too.
///
/// The cross-tenant test lives in DownloadLinkCrossTenantDatabaseTests: this factory keeps all
/// tenants in ONE InMemory database, so "the id exists only in another tenant" cannot be expressed here.
/// </summary>
public class DownloadLinkEndpointTests : IClassFixture<FakvioFactory>
{
    private const long TestCompanyId = 62L;

    /// <summary>A second company WITHOUT EPO header settings - tells the two companies apart in the EPO tests.</summary>
    private const long OtherCompanyId = 63L;

    /// <summary>EPO tax office code of <see cref="TestCompanyId"/>; ends up as c_ufo in the EPO XML.</summary>
    private const int TestCompanyTaxOffice = 451;
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
                CompanyId = TestCompanyId, SchemaName = $"tenant_{TestCompanyId}", IsProvisioned = true, IsActive = true,
                EpoTaxOfficeCode = TestCompanyTaxOffice, EpoTaxOfficeBranchCode = 2017
            });
            masterDb.Client.Add(new Client
            {
                Id = OtherCompanyId, CompanyName = "Other s.r.o.", RegistrationNumber = "T0000063",
                TaxNumber = "CZ00000063", IsIssuer = true, IsActive = true
            });
            masterDb.CompanySystemSettings.Add(new CompanySystemSettings
            {
                CompanyId = OtherCompanyId, SchemaName = $"tenant_{OtherCompanyId}", IsProvisioned = true, IsActive = true
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

        Invoice NewInvoice(string number, EInvoiceStatus status, DateTime issueDate, decimal vatRate) => new()
        {
            DocumentNumber = number, VariableSymbol = "2026062", DocumentType = EDocumentType.Invoice,
            Status = status, IssueDate = issueDate, DueDate = issueDate.AddDays(14),
            Issuer = issuer, Client = customer, Currency = currency,
            TotalBeforeVat = 100m, TotalVat = vatRate, TotalWithVat = 100m + vatRate,
            InvoiceItem =
            [
                new InvoiceItem
                {
                    OrderIndex = 1, Description = "Consulting", Quantity = 1, Unit = "ks", UnitPrice = 100m,
                    VatRatePercentage = vatRate, TotalBeforeVat = 100m, VatAmount = vatRate, TotalWithVat = 100m + vatRate
                }
            ]
        };

        tenantDb.Invoice.Add(NewInvoice("LINK-1", EInvoiceStatus.Completed, new DateTime(2026, 3, 10), 21m));
        // April: a VAT rate Pohoda cannot represent (only 21/12/0 %) -> left out of the accounting export.
        tenantDb.Invoice.Add(NewInvoice("LINK-15PCT", EInvoiceStatus.Completed, new DateTime(2026, 4, 10), 15m));
        // A draft is not ready for eInvoice (UBL) export -> EINVOICE_DRAFT.
        tenantDb.Invoice.Add(NewInvoice("LINK-DRAFT", EInvoiceStatus.Draft, new DateTime(2026, 5, 10), 21m));
        tenantDb.SaveChanges();
    }

    private long InvoiceId(string documentNumber)
    {
        using var scope = _factory.Services.CreateScope();
        return scope.ServiceProvider.GetRequiredService<TenantDbContext>().Invoice.Single(i => i.DocumentNumber == documentNumber).Id;
    }

    /// <summary>A regular (non-SysAdmin) user with an active membership in the test company.</summary>
    private void SeedMember(long userId)
    {
        _factory.SeedRegularUser($"member{userId}@link.test", userId, TestCompanyId);
        using var scope = _factory.Services.CreateScope();
        var masterDb = scope.ServiceProvider.GetRequiredService<MasterDbContext>();
        if (!masterDb.UserCompanyMembership.Any(m => m.UserId == userId && m.CompanyId == TestCompanyId))
        {
            masterDb.UserCompanyMembership.Add(new UserCompanyMembership { UserId = userId, CompanyId = TestCompanyId, Role = EUserRole.User });
            masterDb.SaveChanges();
        }
    }

    /// <summary>Signs a token directly - for tests about REDEEMING a link of a particular user.</summary>
    private string TokenFor(long userId, CreateDownloadLinkDto request)
        => _factory.Services.GetRequiredService<IDownloadLinkProtector>()
            .Protect(new DownloadTokenPayload(TestCompanyId, userId, request), out _);

    private static CreateDownloadLinkDto AccountingRequest() => new()
    {
        Kind = EDownloadKind.AccountingExport,
        AccountingSystem = EAccountingSystem.Pohoda,
        From = new DateTime(2026, 3, 1),
        To = new DateTime(2026, 3, 31)
    };

    /// <summary>Authenticated client (SysAdmin impersonating a company) - used to mint links.</summary>
    private static async Task<HttpClient> LoginAsync(
        Microsoft.AspNetCore.Mvc.Testing.WebApplicationFactory<Program> factory, long? companyId = TestCompanyId)
    {
        var client = factory.CreateClient();
        var login = await AuthHelper.LoginAsSysAdminAsync(client);
        AuthHelper.SetAuthToken(client, login.Token);
        if (companyId.HasValue)
            AuthHelper.SetImpersonation(client, companyId.Value);
        return client;
    }

    /// <summary>
    /// A second host that differs in one setting. Its background workers are dropped: the workers of every
    /// test host hold PostgreSQL connections (advisory locks) until the test run ends, and a local
    /// database allows only 100 of them - these tests need none.
    /// </summary>
    private Microsoft.AspNetCore.Mvc.Testing.WebApplicationFactory<Program> HostWith(string setting, string value)
        => _factory.WithWebHostBuilder(b =>
        {
            b.UseSetting(setting, value);
            b.ConfigureServices(services => services.RemoveAll<IHostedService>());
        });

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
        // The configured lifetime is clamped to at least a minute, so a dead token is signed by hand:
        // same key ring and purpose as DownloadLinkProtector, 1 ms lifetime.
        SeedCompanyWithInvoice(_factory);
        var token = _factory.Services.GetRequiredService<IDataProtectionProvider>()
            .CreateProtector("Fakvio.DownloadLink.v1").ToTimeLimitedDataProtector()
            .Protect(JsonSerializer.Serialize(new DownloadTokenPayload(TestCompanyId, 1, AccountingRequest())), TimeSpan.FromMilliseconds(1));

        await Task.Delay(300);
        var response = await _factory.CreateClient().GetAsync($"/api/download/{token}");

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

    // ── https only + lifetime clamp ─────────────────────────────────────

    [Fact]
    public async Task CreateLink_UsesConfiguredHttpsBaseUrl()
    {
        SeedCompanyWithInvoice(_factory);

        var response = await (await LoginAsync(_factory)).PostAsJsonAsync("/api/download-links", AccountingRequest());

        var link = (await response.Content.ReadFromJsonAsync<DownloadLinkDto>())!;
        link.Url.ShouldStartWith("https://localhost/api/download/");
    }

    [Fact]
    public async Task CreateLink_LifetimeIsClampedToAtMost60Minutes()
    {
        // A SysAdmin typing 1440 must not turn a link into a day-long bearer credential.
        // (The https-only rule of PublicBaseUrl is covered by DownloadLinkControllerTests in Tests.Unit.)
        using var host = HostWith("DownloadLinks:LifetimeMinutes", "1440");
        SeedCompanyWithInvoice(_factory);

        var response = await (await LoginAsync(host)).PostAsJsonAsync("/api/download-links", AccountingRequest());

        var link = (await response.Content.ReadFromJsonAsync<DownloadLinkDto>())!;
        (link.ExpiresAt - DateTime.UtcNow).TotalMinutes.ShouldBe(60, tolerance: 0.5);
    }

    // ── The user is re-validated when the link is redeemed ──────────────

    [Fact]
    public async Task Download_UserDeactivatedAfterMinting_Returns404()
    {
        const long userId = 6201;
        SeedCompanyWithInvoice(_factory);
        SeedMember(userId);
        var token = TokenFor(userId, AccountingRequest());
        (await _factory.CreateClient().GetAsync($"/api/download/{token}")).StatusCode.ShouldBe(HttpStatusCode.OK);

        using (var scope = _factory.Services.CreateScope())
        {
            var masterDb = scope.ServiceProvider.GetRequiredService<MasterDbContext>();
            masterDb.User.Single(u => u.Id == userId).IsActive = false;
            masterDb.SaveChanges();
        }

        var response = await _factory.CreateClient().GetAsync($"/api/download/{token}");

        response.StatusCode.ShouldBe(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task Download_MembershipRemovedAfterMinting_Returns404()
    {
        const long userId = 6202;
        SeedCompanyWithInvoice(_factory);
        SeedMember(userId);
        var token = TokenFor(userId, AccountingRequest());
        (await _factory.CreateClient().GetAsync($"/api/download/{token}")).StatusCode.ShouldBe(HttpStatusCode.OK);

        using (var scope = _factory.Services.CreateScope())
        {
            var masterDb = scope.ServiceProvider.GetRequiredService<MasterDbContext>();
            masterDb.UserCompanyMembership.Single(m => m.UserId == userId && m.CompanyId == TestCompanyId).IsActive = false;
            masterDb.SaveChanges();
        }

        var response = await _factory.CreateClient().GetAsync($"/api/download/{token}");

        response.StatusCode.ShouldBe(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task Download_UserWhoNeverBelongedToTheCompany_Returns404()
    {
        // A correctly signed token is still not enough: user 6203 has no membership in the token's company.
        SeedCompanyWithInvoice(_factory);
        _factory.SeedRegularUser("outsider6203@link.test", 6203);

        var response = await _factory.CreateClient().GetAsync($"/api/download/{TokenFor(6203, AccountingRequest())}");

        response.StatusCode.ShouldBe(HttpStatusCode.NotFound);
    }

    // ── The token decides, not whatever credential is sent along ────────

    [Theory]
    [InlineData(OtherCompanyId)] // somebody signed in to ANOTHER company
    [InlineData(null)]           // a SysAdmin who is not impersonating anyone
    public async Task Download_WithForeignJwtInAuthorizationHeader_TokenWins(long? jwtCompanyId)
    {
        // The EPO return is built from the company in the request's claims (EPO header settings in the
        // master DB). Company 62 has them (c_ufo 451), company 63 has none. If the JWT's company leaked
        // into the download, this would answer 400 EPO_HEADER_INCOMPLETE instead of company 62's file.
        SeedCompanyWithInvoice(_factory);
        var token = await MintTokenAsync(await LoginAsync(_factory), new CreateDownloadLinkDto
        {
            Kind = EDownloadKind.VatEpoReturn, Year = 2026, Period = 3, PeriodType = EVatPeriodType.Monthly
        });
        var foreign = await LoginAsync(_factory, jwtCompanyId);

        var response = await foreign.GetAsync($"/api/download/{token}");

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        (await response.Content.ReadAsStringAsync()).ShouldContain($"c_ufo=\"{TestCompanyTaxOffice}\"");
    }

    // ── Refused exports are reported when the link is MINTED ────────────

    [Fact]
    public async Task CreateLink_UblForInvoiceThatIsNotReady_Returns400TenantNotReady_WithEinvoiceIssues()
    {
        // Same body as GET /api/invoice/{id}/ubl (#342): the MCP tool turns it into a structured answer
        // for the AI. Without this the user would open a link and see raw JSON.
        SeedCompanyWithInvoice(_factory);

        var response = await (await LoginAsync(_factory)).PostAsJsonAsync("/api/download-links",
            new CreateDownloadLinkDto { Kind = EDownloadKind.InvoiceUbl, Id = InvoiceId("LINK-DRAFT") });

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        doc.RootElement.GetProperty("code").GetString().ShouldBe("TENANT_NOT_READY");
        doc.RootElement.GetProperty("issues").EnumerateArray()
            .Select(i => i.GetProperty("code").GetString()).ShouldContain("EINVOICE_DRAFT");
        doc.RootElement.TryGetProperty("url", out _).ShouldBeFalse();
    }

    [Fact]
    public async Task CreateLink_EpoWithIncompleteHeader_Returns400EpoHeaderIncomplete()
    {
        SeedCompanyWithInvoice(_factory);

        var response = await (await LoginAsync(_factory, OtherCompanyId)).PostAsJsonAsync("/api/download-links",
            new CreateDownloadLinkDto { Kind = EDownloadKind.VatEpoReturn, Year = 2026, Period = 3, PeriodType = EVatPeriodType.Monthly });

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        doc.RootElement.GetProperty("code").GetString().ShouldBe("EPO_HEADER_INCOMPLETE");
        doc.RootElement.GetProperty("missingFields").EnumerateArray()
            .Select(f => f.GetString()).ShouldContain("EpoTaxOfficeCode");
    }

    [Theory]
    [InlineData(3, 0)] // March: LINK-1 (21 %) is exportable
    [InlineData(4, 1)] // April: LINK-15PCT has a VAT rate Pohoda cannot represent
    public async Task CreateLink_AccountingExport_ReportsSkippedDocuments(int month, int expectedSkipped)
    {
        SeedCompanyWithInvoice(_factory);

        var response = await (await LoginAsync(_factory)).PostAsJsonAsync("/api/download-links", new CreateDownloadLinkDto
        {
            Kind = EDownloadKind.AccountingExport, AccountingSystem = EAccountingSystem.Pohoda,
            From = new DateTime(2026, month, 1), To = new DateTime(2026, month, 28)
        });

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        (await response.Content.ReadFromJsonAsync<DownloadLinkDto>())!.SkippedDocuments.ShouldBe(expectedSkipped);
        // The count travels in the body; the dry run's file headers must not stay on this (JSON) response.
        response.Headers.Contains("X-Export-Skipped").ShouldBeFalse();
        response.Headers.Contains("X-Export-Exported").ShouldBeFalse();
    }

    // ── Other kinds end to end (each dispatch arm builds its controller by hand) ──

    [Fact]
    public async Task Download_SingleDocumentKind_InvoiceIsdoc_ReturnsTheFile()
    {
        SeedCompanyWithInvoice(_factory);
        var token = await MintTokenAsync(await LoginAsync(_factory),
            new CreateDownloadLinkDto { Kind = EDownloadKind.InvoiceIsdoc, Id = InvoiceId("LINK-1") });

        var file = await _factory.CreateClient().GetAsync($"/api/download/{token}");

        file.StatusCode.ShouldBe(HttpStatusCode.OK);
        file.Content.Headers.ContentDisposition!.DispositionType.ShouldBe("attachment");
        (await file.Content.ReadAsStringAsync()).ShouldContain("LINK-1");
    }

    [Fact]
    public async Task Download_BulkKind_InvoiceBulkIsdoc_ReturnsZip()
    {
        SeedCompanyWithInvoice(_factory);
        var token = await MintTokenAsync(await LoginAsync(_factory),
            new CreateDownloadLinkDto { Kind = EDownloadKind.InvoiceBulkIsdoc, Ids = [InvoiceId("LINK-1"), InvoiceId("LINK-15PCT")] });

        var file = await _factory.CreateClient().GetAsync($"/api/download/{token}");

        file.StatusCode.ShouldBe(HttpStatusCode.OK);
        file.Content.Headers.ContentType!.MediaType.ShouldBe("application/zip");
        using var zip = new System.IO.Compression.ZipArchive(await file.Content.ReadAsStreamAsync());
        zip.Entries.Count.ShouldBe(2);
    }

    // ── Rate limiting of the anonymous endpoint ─────────────────────────

    [Fact]
    public async Task Download_ExceedsPermitLimit_Returns429_WithRetryAfter()
    {
        using var tight = HostWith("RateLimiting:DownloadLink:PermitLimit", "2");
        var client = tight.CreateClient();

        for (var i = 0; i < 2; i++)
            (await client.GetAsync("/api/download/not-a-token")).StatusCode.ShouldBe(HttpStatusCode.NotFound);
        var rejected = await client.GetAsync("/api/download/not-a-token");

        rejected.StatusCode.ShouldBe(HttpStatusCode.TooManyRequests);
        rejected.Headers.RetryAfter.ShouldNotBeNull();
    }
}

using System.Net;
using System.Net.Http.Json;
using Fakvio.Contracts.Dto.Client;
using Fakvio.Domain.Entities;
using Fakvio.Domain.Enums;
using Fakvio.Infrastructure.Data;
using Fakvio.Tests.Integration.Fixtures;
using Fakvio.Tests.Integration.Helpers;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;

namespace Fakvio.Tests.Integration;

/// <summary>
/// File names of generated documents come from the configurable format
/// (client -> issuer -> system default) and are delivered via Content-Disposition.
///
/// Test data is seeded through a SEPARATE DI scope from the request the API handles, so the
/// service under test has to load the BillingSettings itself (no hidden change-tracker fix-up).
/// </summary>
public class DocumentFileNameEndpointTests : IClassFixture<FakvioFactory>
{
    private readonly FakvioFactory _factory;
    private const long CompanyId = 7900L;
    private const long IssuerId = 7900L;
    private const long CustomerWithOwnFormat = 7901L;
    private const long CustomerInheriting = 7902L;
    private const long EnglishCustomer = 7903L;
    private const long InvoiceOwnFormat = 7901L;
    private const long InvoiceInheriting = 7902L;
    private const long CreditNoteEnglish = 7903L;

    public DocumentFileNameEndpointTests(FakvioFactory factory)
    {
        _factory = factory;
        _factory.InitializeDatabase();
        Seed();
    }

    private void Seed()
    {
        using var scope = _factory.Services.CreateScope();
        var masterDb = scope.ServiceProvider.GetRequiredService<MasterDbContext>();
        var tenantDb = scope.ServiceProvider.GetRequiredService<TenantDbContext>();

        if (!masterDb.Client.Any(c => c.Id == CompanyId))
        {
            masterDb.Client.Add(new Client
            {
                Id = CompanyId, CompanyName = "Format Issuer", RegistrationNumber = "FN000001",
                IsIssuer = true, IsActive = true
            });
            masterDb.CompanySystemSettings.Add(new CompanySystemSettings
            {
                CompanyId = CompanyId, SchemaName = $"tenant_fn_{CompanyId}", IsProvisioned = true, IsActive = true
            });
            masterDb.SaveChanges();
        }

        if (tenantDb.Client.Any(c => c.Id == IssuerId))
            return; // already seeded by an earlier test in this class

        // Issuer with a company-wide default for invoices only
        tenantDb.Client.Add(new Client
        {
            Id = IssuerId, CompanyName = "Format Issuer", RegistrationNumber = "FN000001",
            IsIssuer = true, IsActive = true,
            BillingSettings = new BillingSettings { InvoiceFileNameFormat = "{{IssuerName}}-{{DocumentNumber}}" }
        });
        tenantDb.Client.Add(new Client
        {
            Id = CustomerWithOwnFormat, CompanyName = "Customer A", RegistrationNumber = "FN000002", IsActive = true,
            BillingSettings = new BillingSettings { InvoiceFileNameFormat = "{{Year}}_{{ClientName}}_{{DocumentNumber}}" }
        });
        tenantDb.Client.Add(new Client
        {
            Id = CustomerInheriting, CompanyName = "Customer B", RegistrationNumber = "FN000003", IsActive = true
        });
        tenantDb.Client.Add(new Client
        {
            Id = EnglishCustomer, CompanyName = "Customer C", RegistrationNumber = "FN000004", IsActive = true, Language = "en"
        });
        tenantDb.SaveChanges();

        AddInvoice(tenantDb, InvoiceOwnFormat, CustomerWithOwnFormat, EDocumentType.Invoice, "FV1");
        AddInvoice(tenantDb, InvoiceInheriting, CustomerInheriting, EDocumentType.Invoice, "FV2");
        AddInvoice(tenantDb, CreditNoteEnglish, EnglishCustomer, EDocumentType.CreditNote, "DB3");
        tenantDb.SaveChanges();
    }

    private static void AddInvoice(TenantDbContext db, long id, long clientId, EDocumentType type, string number) =>
        db.Invoice.Add(new Invoice
        {
            Id = id, DocumentType = type, Status = EInvoiceStatus.Completed, DocumentNumber = number,
            IssueDate = new DateTime(2026, 3, 5), DueDate = new DateTime(2026, 3, 19),
            IssuerId = IssuerId, ClientId = clientId, CurrencyId = 1
        });

    private async Task<HttpClient> AuthClientAsync()
    {
        var client = _factory.CreateClient();
        var login = await AuthHelper.LoginAsSysAdminAsync(client);
        AuthHelper.SetAuthToken(client, login.Token);
        AuthHelper.SetImpersonation(client, CompanyId);
        return client;
    }

    private static string? DownloadName(HttpResponseMessage response)
    {
        var disposition = response.Content.Headers.ContentDisposition;
        return disposition?.FileNameStar ?? disposition?.FileName?.Trim('"');
    }

    [Fact]
    public async Task GetPdf_ClientHasFormat_ContentDispositionUsesIt()
    {
        var client = await AuthClientAsync();

        var response = await client.GetAsync($"/api/invoice/{InvoiceOwnFormat}/pdf");

        response.StatusCode.ShouldBe(HttpStatusCode.OK, await response.Content.ReadAsStringAsync());
        DownloadName(response).ShouldBe("2026_Customer A_FV1.pdf");
    }

    [Fact]
    public async Task GetIsdoc_UsesTheSameFormatAsThePdf()
    {
        var client = await AuthClientAsync();

        var response = await client.GetAsync($"/api/invoice/{InvoiceOwnFormat}/isdoc");

        response.StatusCode.ShouldBe(HttpStatusCode.OK, await response.Content.ReadAsStringAsync());
        DownloadName(response).ShouldBe("2026_Customer A_FV1.isdoc");
    }

    [Fact]
    public async Task GetPdf_ClientHasNoFormat_IssuerDefaultIsUsed()
    {
        var client = await AuthClientAsync();

        var response = await client.GetAsync($"/api/invoice/{InvoiceInheriting}/pdf");

        response.StatusCode.ShouldBe(HttpStatusCode.OK, await response.Content.ReadAsStringAsync());
        DownloadName(response).ShouldBe("Format Issuer-FV2.pdf");
    }

    [Fact]
    public async Task GetPdf_NoFormatAnywhereForTheType_UsesLocalizedSystemDefault()
    {
        // The issuer only configured invoices; this English client's credit note gets the system default.
        var client = await AuthClientAsync();

        var response = await client.GetAsync($"/api/invoice/{CreditNoteEnglish}/pdf");

        response.StatusCode.ShouldBe(HttpStatusCode.OK, await response.Content.ReadAsStringAsync());
        DownloadName(response).ShouldBe("CreditNote_DB3.pdf");
    }

    [Fact]
    public async Task BulkPdf_EntriesAreNamedByEachClientsFormat()
    {
        var client = await AuthClientAsync();

        var response = await client.GetAsync($"/api/invoice/bulk/pdf?ids={InvoiceOwnFormat},{InvoiceInheriting}");

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        using var zip = new System.IO.Compression.ZipArchive(await response.Content.ReadAsStreamAsync());
        zip.Entries.Select(e => e.Name).OrderBy(n => n).ShouldBe(
            ["2026_Customer A_FV1.pdf", "Format Issuer-FV2.pdf"]);
    }

    [Fact]
    public async Task UpdateClient_UnknownPlaceholder_Returns400WithTheKeyName()
    {
        var client = await AuthClientAsync();

        var response = await client.PutAsJsonAsync($"/api/client/{CustomerInheriting}", new UpdateClientDto
        {
            BillingSettings = new UpdateBillingSettingsDto { InvoiceFileNameFormat = "{{DocumentNumber}}_{{Oops}}" }
        });

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await response.Content.ReadAsStringAsync()).ShouldContain("Oops");
    }

    [Fact]
    public async Task UpdateClient_ValidFormat_IsUsedByTheNextDownload()
    {
        var client = await AuthClientAsync();

        var update = await client.PutAsJsonAsync($"/api/client/{EnglishCustomer}", new UpdateClientDto
        {
            BillingSettings = new UpdateBillingSettingsDto { CreditNoteFileNameFormat = "{{DocumentType}} {{DocumentNumber}}" }
        });
        update.StatusCode.ShouldBe(HttpStatusCode.OK, await update.Content.ReadAsStringAsync());

        var response = await client.GetAsync($"/api/invoice/{CreditNoteEnglish}/pdf");

        DownloadName(response).ShouldBe("CreditNote DB3.pdf");

        // Blank clears the setting again -> back to the system default
        await client.PutAsJsonAsync($"/api/client/{EnglishCustomer}", new UpdateClientDto
        {
            BillingSettings = new UpdateBillingSettingsDto { CreditNoteFileNameFormat = "" }
        });
        DownloadName(await client.GetAsync($"/api/invoice/{CreditNoteEnglish}/pdf")).ShouldBe("CreditNote_DB3.pdf");
    }
}

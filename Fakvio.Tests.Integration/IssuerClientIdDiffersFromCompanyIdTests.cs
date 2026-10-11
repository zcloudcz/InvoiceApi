using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Fakvio.Application.Service;
using Fakvio.Contracts.Dto.Dashboard;
using Fakvio.Domain.Entities;
using Fakvio.Domain.Enums;
using Fakvio.Infrastructure.Data;
using Fakvio.Infrastructure.Service.ChatTools;
using Fakvio.Tests.Integration.Fixtures;
using Fakvio.Tests.Integration.Helpers;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;

namespace Fakvio.Tests.Integration;

/// <summary>
/// Regression for the "company id == issuer client id" assumption (PR #537 review, a real production bug).
///
/// The CompanyId claim is the company's id in the MASTER database. <c>Invoice.IssuerId</c> is the id of the
/// tenant's own-company client (<c>Client.IsIssuer</c>) inside the TENANT schema. Nothing keeps the two equal
/// (in production every tenant's issuer client has Id 1 while company ids are 1, 3, 4, ...). The lists, the
/// dashboard and the chat tool used to filter <c>IssuerId == CompanyId</c> and so showed such a tenant nothing.
///
/// Here the two ids differ on purpose. The rows are seeded through the factory's own scope (a DbContext separate
/// from the ones serving the requests), so EF relationship fix-up cannot paper over a missing Include.
/// </summary>
public class IssuerClientIdDiffersFromCompanyIdTests : IClassFixture<FakvioFactory>
{
    private readonly FakvioFactory _factory;

    private const long CompanyId = 9200L;      // master database id (the CompanyId claim)
    private const long IssuerClientId = 9207L; // our company INSIDE the tenant - deliberately a different number
    private const long PartnerId = 9208L;      // customer of the issued invoice, supplier of the received one
    private const long CurrencyId = 1L;        // CZK seeded by InitializeDatabase()

    public IssuerClientIdDiffersFromCompanyIdTests(FakvioFactory factory)
    {
        _factory = factory;
        _factory.InitializeDatabase();
        SeedTenant();
    }

    private void SeedTenant()
    {
        using var scope = _factory.Services.CreateScope();
        var masterDb = scope.ServiceProvider.GetRequiredService<MasterDbContext>();
        var tenantDb = scope.ServiceProvider.GetRequiredService<TenantDbContext>();

        if (!masterDb.Client.Any(c => c.Id == CompanyId))
            masterDb.Client.Add(new Client
            {
                Id = CompanyId, CompanyName = "Issuer Id Test Co", RegistrationNumber = "II000001", IsIssuer = true, IsActive = true
            });
        if (!masterDb.CompanySystemSettings.Any(s => s.CompanyId == CompanyId))
            masterDb.CompanySystemSettings.Add(new CompanySystemSettings
            {
                CompanyId = CompanyId, SchemaName = $"tenant_ii_{CompanyId}", IsProvisioned = true, IsActive = true
            });
        masterDb.SaveChanges();

        if (!tenantDb.Client.Any(c => c.Id == IssuerClientId))
            tenantDb.Client.Add(new Client
            {
                Id = IssuerClientId, CompanyName = "Issuer Id Test Co", RegistrationNumber = "II000001", IsIssuer = true, IsActive = true
            });
        if (!tenantDb.Client.Any(c => c.Id == PartnerId))
            tenantDb.Client.Add(new Client
            {
                Id = PartnerId, CompanyName = "Issuer Id Partner", RegistrationNumber = "II000002", IsIssuer = false, IsActive = true
            });
        tenantDb.SaveChanges();
    }

    /// <summary>A Completed document due this month, so the dashboard's "due this month" aggregate counts it.</summary>
    private (long Id, string Number) SeedDoc(EDocumentType type)
    {
        using var scope = _factory.Services.CreateScope();
        var tenantDb = scope.ServiceProvider.GetRequiredService<TenantDbContext>();

        var now = DateTime.UtcNow;
        var invoice = new Invoice
        {
            DocumentNumber = $"II-{Guid.NewGuid():N}"[..14],
            DocumentType = type, Status = EInvoiceStatus.Completed,
            ClientId = PartnerId, IssuerId = IssuerClientId, CurrencyId = CurrencyId,
            VariableSymbol = Random.Shared.NextInt64(1_000_000_000, 9_999_999_999).ToString(),
            IssueDate = now, TaxableSupplyDate = now,
            // Middle of the current month: always inside the dashboard's [first day, first day of next month) window.
            DueDate = new DateTime(now.Year, now.Month, 15, 0, 0, 0, DateTimeKind.Utc),
            ReceivedDate = type == EDocumentType.ReceivedInvoice ? now : null,
            TotalBeforeVat = 100m, TotalVat = 21m, TotalWithVat = 121m
        };
        tenantDb.Invoice.Add(invoice);
        tenantDb.SaveChanges();
        return (invoice.Id, invoice.DocumentNumber);
    }

    private async Task<HttpClient> CreateAuthenticatedClientAsync()
    {
        var client = _factory.CreateClient();
        var login = await AuthHelper.LoginAsSysAdminAsync(client);
        AuthHelper.SetAuthToken(client, login.Token);
        AuthHelper.SetImpersonation(client, CompanyId); // puts CompanyId = 9200 into the request's claims
        return client;
    }

    private static async Task<List<long>> IdsOf(HttpResponseMessage response, string? arrayProperty = null)
    {
        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var array = arrayProperty is null ? doc.RootElement : doc.RootElement.GetProperty(arrayProperty);
        return array.EnumerateArray().Select(i => i.GetProperty("id").GetInt64()).ToList();
    }

    [Fact]
    public async Task List_ShowsIssuedAndReceivedDocuments_OfATenantWhoseIssuerClientIdIsNotTheCompanyId()
    {
        var issued = SeedDoc(EDocumentType.Invoice);
        var received = SeedDoc(EDocumentType.ReceivedInvoice);
        var client = await CreateAuthenticatedClientAsync();

        (await IdsOf(await client.GetAsync("/api/invoice"))).ShouldContain(issued.Id);
        (await IdsOf(await client.GetAsync($"/api/invoice?documentType={(int)EDocumentType.ReceivedInvoice}")))
            .ShouldContain(received.Id);
    }

    [Fact]
    public async Task PagedList_ShowsIssuedAndReceivedDocuments_OfATenantWhoseIssuerClientIdIsNotTheCompanyId()
    {
        var issued = SeedDoc(EDocumentType.Invoice);
        var received = SeedDoc(EDocumentType.ReceivedInvoice);
        var client = await CreateAuthenticatedClientAsync();

        (await IdsOf(await client.GetAsync("/api/invoice/paged?Page=1&PageSize=100"), "items")).ShouldContain(issued.Id);
        (await IdsOf(await client.GetAsync(
            $"/api/invoice/paged?Page=1&PageSize=100&DocumentTypes={(int)EDocumentType.ReceivedInvoice}"), "items"))
            .ShouldContain(received.Id);
    }

    /// <summary>
    /// The issuer filter itself stays: a caller that picks a real issuer client id gets that issuer's documents,
    /// and an id that is no issuer of this tenant (here: the company id) matches nothing.
    /// </summary>
    [Fact]
    public async Task ExplicitIssuerFilter_IsAppliedAsGiven_OnBothLists()
    {
        var issued = SeedDoc(EDocumentType.Invoice);
        var client = await CreateAuthenticatedClientAsync();

        (await IdsOf(await client.GetAsync($"/api/invoice?issuerId={IssuerClientId}"))).ShouldContain(issued.Id);
        (await IdsOf(await client.GetAsync($"/api/invoice/paged?Page=1&PageSize=100&IssuerId={IssuerClientId}"), "items"))
            .ShouldContain(issued.Id);

        (await IdsOf(await client.GetAsync($"/api/invoice?issuerId={CompanyId}"))).ShouldBeEmpty();
        (await IdsOf(await client.GetAsync($"/api/invoice/paged?Page=1&PageSize=100&IssuerId={CompanyId}"), "items"))
            .ShouldBeEmpty();
    }

    [Fact]
    public async Task Dashboard_CountsIssuedAndReceivedDocuments_OfATenantWhoseIssuerClientIdIsNotTheCompanyId()
    {
        var issued = SeedDoc(EDocumentType.Invoice);
        SeedDoc(EDocumentType.ReceivedInvoice);
        var client = await CreateAuthenticatedClientAsync();

        var response = await client.GetAsync("/api/dashboard");

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        var dto = (await response.Content.ReadFromJsonAsync<DashboardDto>())!;
        dto.RecentInvoices.Select(i => i.Id).ShouldContain(issued.Id);
        dto.InvoicesDueThisMonthCount.ShouldBeGreaterThan(0);
        dto.IncomeVsExpenseByMonth.Sum(m => m.Income).ShouldBeGreaterThan(0m);  // the issued invoice
        dto.IncomeVsExpenseByMonth.Sum(m => m.Expense).ShouldBeGreaterThan(0m); // the received invoice
    }

    /// <summary>
    /// The chat tool is built over the REAL invoice service and database (only its logger is a stand-in), so the
    /// assertion covers the whole path the AI assistant uses.
    /// </summary>
    [Fact]
    public async Task ChatListInvoicesTool_ShowsIssuedAndReceivedDocuments_OfATenantWhoseIssuerClientIdIsNotTheCompanyId()
    {
        var issued = SeedDoc(EDocumentType.Invoice);
        var received = SeedDoc(EDocumentType.ReceivedInvoice);

        using var scope = _factory.Services.CreateScope();
        var tool = scope.ServiceProvider.GetServices<IChatTool>().OfType<ListInvoicesTool>().Single();

        var issuedResult = await tool.ExecuteAsync(new Dictionary<string, string> { ["page_size"] = "50" });
        var receivedResult = await tool.ExecuteAsync(new Dictionary<string, string>
        {
            ["page_size"] = "50", ["document_type"] = nameof(EDocumentType.ReceivedInvoice)
        });

        issuedResult.IsSuccess.ShouldBeTrue();
        issuedResult.OutputText.ShouldContain(issued.Number);
        receivedResult.IsSuccess.ShouldBeTrue();
        receivedResult.OutputText.ShouldContain(received.Number);
    }
}

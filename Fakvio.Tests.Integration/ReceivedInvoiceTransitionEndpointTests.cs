using System.Net;
using System.Text.Json;
using Fakvio.Domain.Entities;
using Fakvio.Domain.Enums;
using Fakvio.Infrastructure.Data;
using Fakvio.Tests.Integration.Fixtures;
using Fakvio.Tests.Integration.Helpers;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;

namespace Fakvio.Tests.Integration;

/// <summary>
/// HTTP-level tests of the received-invoice transitions that mirror the issued routes
/// (<c>POST api/received-invoice/{id}/mark-unpaid | revert-to-draft | restore</c>) and of the by-id isolation:
/// an issued route given a received id (and vice versa) answers 404 and leaves the document untouched.
/// </summary>
public class ReceivedInvoiceTransitionEndpointTests : IClassFixture<FakvioFactory>
{
    private readonly FakvioFactory _factory;

    private const long CompanyId = 8900L;
    private const long SupplierId = 8901L;
    private const long CurrencyId = 1L; // CZK seeded by InitializeDatabase()

    public ReceivedInvoiceTransitionEndpointTests(FakvioFactory factory)
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
                Id = CompanyId, CompanyName = "Transition Test Co", RegistrationNumber = "TT000001", IsIssuer = true, IsActive = true
            });
        if (!masterDb.CompanySystemSettings.Any(s => s.CompanyId == CompanyId))
            masterDb.CompanySystemSettings.Add(new CompanySystemSettings
            {
                CompanyId = CompanyId, SchemaName = $"tenant_tr_{CompanyId}", IsProvisioned = true, IsActive = true
            });
        masterDb.SaveChanges();

        if (!tenantDb.Client.Any(c => c.Id == SupplierId))
        {
            tenantDb.Client.Add(new Client
            {
                Id = SupplierId, CompanyName = "Transition Supplier", RegistrationNumber = "TT000002", IsIssuer = false, IsActive = true
            });
            tenantDb.SaveChanges();
        }
    }

    private long SeedDoc(EDocumentType type, EInvoiceStatus status, decimal paidAmount = 0m)
    {
        using var scope = _factory.Services.CreateScope();
        var tenantDb = scope.ServiceProvider.GetRequiredService<TenantDbContext>();

        var invoice = new Invoice
        {
            DocumentNumber = $"TR-{Guid.NewGuid():N}"[..14],
            DocumentType = type, Status = status, ClientId = SupplierId, IssuerId = CompanyId, CurrencyId = CurrencyId,
            VariableSymbol = Random.Shared.NextInt64(1_000_000_000, 9_999_999_999).ToString(),
            IssueDate = DateTime.UtcNow.AddDays(-5), DueDate = DateTime.UtcNow.AddDays(10), TaxableSupplyDate = DateTime.UtcNow.AddDays(-5),
            ReceivedDate = type == EDocumentType.ReceivedInvoice ? DateTime.UtcNow : null,
            TotalBeforeVat = 100m, TotalVat = 21m, TotalWithVat = 121m, PaidAmount = paidAmount,
            PaidAt = paidAmount > 0 ? DateTime.UtcNow : null
        };
        tenantDb.Invoice.Add(invoice);
        tenantDb.SaveChanges();
        return invoice.Id;
    }

    private EInvoiceStatus StatusOf(long id)
    {
        using var scope = _factory.Services.CreateScope();
        var tenantDb = scope.ServiceProvider.GetRequiredService<TenantDbContext>();
        return tenantDb.Invoice.Single(i => i.Id == id).Status;
    }

    private async Task<HttpClient> CreateAuthenticatedClientAsync()
    {
        var client = _factory.CreateClient();
        var login = await AuthHelper.LoginAsSysAdminAsync(client);
        AuthHelper.SetAuthToken(client, login.Token);
        AuthHelper.SetImpersonation(client, CompanyId);
        return client;
    }

    private static async Task<string?> LegacyStatusAsync(HttpResponseMessage response)
    {
        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return doc.RootElement.GetProperty("status").ToString();
    }

    [Fact]
    public async Task PaidReceivedInvoice_FullWayBack_MarkUnpaid_RevertToDraft_Delete_Restore()
    {
        var id = SeedDoc(EDocumentType.ReceivedInvoice, EInvoiceStatus.Paid, paidAmount: 121m);
        var client = await CreateAuthenticatedClientAsync();

        var unpaid = await client.PostAsync($"/api/received-invoice/{id}/mark-unpaid", null);
        unpaid.StatusCode.ShouldBe(HttpStatusCode.OK);
        StatusOf(id).ShouldBe(EInvoiceStatus.Completed);

        var draft = await client.PostAsync($"/api/received-invoice/{id}/revert-to-draft", null);
        draft.StatusCode.ShouldBe(HttpStatusCode.OK);
        StatusOf(id).ShouldBe(EInvoiceStatus.Draft);

        (await client.DeleteAsync($"/api/received-invoice/{id}")).StatusCode.ShouldBe(HttpStatusCode.NoContent);
        StatusOf(id).ShouldBe(EInvoiceStatus.Deleted);

        var restored = await client.PostAsync($"/api/received-invoice/{id}/restore", null);
        restored.StatusCode.ShouldBe(HttpStatusCode.OK);
        StatusOf(id).ShouldBe(EInvoiceStatus.Draft);
    }

    [Fact]
    public async Task ForbiddenTransitions_Return400_AndChangeNothing()
    {
        var paid = SeedDoc(EDocumentType.ReceivedInvoice, EInvoiceStatus.Paid, paidAmount: 121m);
        var draft = SeedDoc(EDocumentType.ReceivedInvoice, EInvoiceStatus.Draft);
        var client = await CreateAuthenticatedClientAsync();

        (await client.PostAsync($"/api/received-invoice/{paid}/revert-to-draft", null)).StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await client.DeleteAsync($"/api/received-invoice/{paid}")).StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await client.PostAsync($"/api/received-invoice/{draft}/mark-unpaid", null)).StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await client.PostAsync($"/api/received-invoice/{draft}/restore", null)).StatusCode.ShouldBe(HttpStatusCode.BadRequest);

        StatusOf(paid).ShouldBe(EInvoiceStatus.Paid);
        StatusOf(draft).ShouldBe(EInvoiceStatus.Draft);
    }

    [Fact]
    public async Task PartiallyPaidReceivedInvoice_IsApprovedInTheLegacyDto_AndCanBeMarkedUnpaid()
    {
        var id = SeedDoc(EDocumentType.ReceivedInvoice, EInvoiceStatus.PartiallyPaid, paidAmount: 50m);
        var client = await CreateAuthenticatedClientAsync();

        var get = await client.GetAsync($"/api/received-invoice/{id}");
        get.StatusCode.ShouldBe(HttpStatusCode.OK);
        // EReceivedInvoiceStatus.Approved = 2 (serialised either as number or as name, depending on the JSON options).
        (await LegacyStatusAsync(get)).ShouldBeOneOf("2", "Approved");

        (await client.PostAsync($"/api/received-invoice/{id}/mark-unpaid", null)).StatusCode.ShouldBe(HttpStatusCode.OK);
        StatusOf(id).ShouldBe(EInvoiceStatus.Completed);
    }

    [Theory]
    [InlineData("mark-unpaid", EInvoiceStatus.Paid)]
    [InlineData("revert-to-draft", EInvoiceStatus.Completed)]
    [InlineData("restore", EInvoiceStatus.Deleted)]
    public async Task ReceivedRoutes_GivenIssuedId_Return404_AndLeaveTheDocumentUntouched(string route, EInvoiceStatus status)
    {
        var issuedId = SeedDoc(EDocumentType.Invoice, status, paidAmount: status == EInvoiceStatus.Paid ? 121m : 0m);
        var client = await CreateAuthenticatedClientAsync();

        (await client.PostAsync($"/api/received-invoice/{issuedId}/{route}", null)).StatusCode.ShouldBe(HttpStatusCode.NotFound);
        (await client.PostAsync($"/api/received-invoice/{issuedId}/mark-paid", null)).StatusCode.ShouldBe(HttpStatusCode.NotFound);
        (await client.DeleteAsync($"/api/received-invoice/{issuedId}")).StatusCode.ShouldBe(HttpStatusCode.NotFound);

        StatusOf(issuedId).ShouldBe(status);
    }

    [Theory]
    [InlineData("mark-unpaid", EInvoiceStatus.Paid)]
    [InlineData("revert-to-draft", EInvoiceStatus.Completed)]
    [InlineData("restore", EInvoiceStatus.Deleted)]
    public async Task IssuedRoutes_GivenReceivedId_Return404_AndLeaveTheDocumentUntouched(string route, EInvoiceStatus status)
    {
        var receivedId = SeedDoc(EDocumentType.ReceivedInvoice, status, paidAmount: status == EInvoiceStatus.Paid ? 121m : 0m);
        var client = await CreateAuthenticatedClientAsync();

        (await client.PostAsync($"/api/invoice/{receivedId}/{route}", null)).StatusCode.ShouldBe(HttpStatusCode.NotFound);
        (await client.DeleteAsync($"/api/invoice/{receivedId}")).StatusCode.ShouldBe(HttpStatusCode.NotFound);

        StatusOf(receivedId).ShouldBe(status);
    }
}

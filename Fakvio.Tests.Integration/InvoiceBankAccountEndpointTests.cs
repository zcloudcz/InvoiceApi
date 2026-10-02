using System.Net;
using System.Net.Http.Json;
using Fakvio.Contracts.Dto.Invoice;
using Fakvio.Domain.Entities;
using Fakvio.Domain.Enums;
using Fakvio.Infrastructure.Data;
using Fakvio.Tests.Integration.Fixtures;
using Fakvio.Tests.Integration.Helpers;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;

namespace Fakvio.Tests.Integration;

/// <summary>
/// POST /api/invoice without any bank data must pick the issuer's default account;
/// a BankAccountId of another company must be rejected with 400. Also covers PUT /api/invoice/{id}
/// (partial update, items replacement) and revert-to-draft used by the MCP update_invoice flow.
/// </summary>
public class InvoiceBankAccountEndpointTests : IClassFixture<FakvioFactory>
{
    private readonly FakvioFactory _factory;
    private const long CompanyId = 7800L;
    private const long IssuerId = 7800L;
    private const long CustomerId = 7801L;
    private const long OtherIssuerId = 7802L;

    public InvoiceBankAccountEndpointTests(FakvioFactory factory)
    {
        _factory = factory;
        _factory.InitializeDatabase();
        SeedScenario();
    }

    private void SeedScenario()
    {
        using var scope = _factory.Services.CreateScope();
        var masterDb = scope.ServiceProvider.GetRequiredService<MasterDbContext>();
        var tenantDb = scope.ServiceProvider.GetRequiredService<TenantDbContext>();

        // ── Master DB ───────────────────────────────────────────────────────

        if (!masterDb.Client.Any(c => c.Id == CompanyId))
        {
            masterDb.Client.Add(new Client
            {
                Id = CompanyId,
                CompanyName = "Bank Test Company",
                RegistrationNumber = "BT000001",
                IsIssuer = true,
                IsActive = true
            });
        }

        if (!masterDb.CompanySystemSettings.Any(s => s.CompanyId == CompanyId))
        {
            masterDb.CompanySystemSettings.Add(new CompanySystemSettings
            {
                CompanyId = CompanyId,
                SchemaName = $"tenant_bank_{CompanyId}",
                IsProvisioned = true,
                IsActive = true
            });
        }

        masterDb.SaveChanges();

        // ── Tenant DB ───────────────────────────────────────────────────────

        // Issuer (the company issuing invoices)
        if (!tenantDb.Client.Any(c => c.Id == IssuerId))
        {
            tenantDb.Client.Add(new Client
            {
                Id = IssuerId,
                CompanyName = "Bank Test Company",
                RegistrationNumber = "BT000001",
                IsIssuer = true,
                IsActive = true,
                IsVatPayer = false // non-VAT payer keeps items simple
            });
            tenantDb.SaveChanges();
        }

        // Customer
        if (!tenantDb.Client.Any(c => c.Id == CustomerId))
        {
            tenantDb.Client.Add(new Client
            {
                Id = CustomerId,
                CompanyName = "Bank Test Customer",
                RegistrationNumber = "BT000002",
                IsIssuer = false,
                IsActive = true
            });
            tenantDb.SaveChanges();
        }

        // NOTE: No NumberSequence is seeded here — the InvoiceService fallback in
        // GenerateDocumentNumberAsync handles missing sequences gracefully by using
        // a simple year-based counter (e.g., "INV20260001"). This keeps the seed
        // minimal and avoids the need to also seed NumberSequenceFormat.

        // Foreign issuer + accounts (default one for our issuer, plus one of another company)
        if (!tenantDb.Client.Any(c => c.Id == OtherIssuerId))
        {
            tenantDb.Client.Add(new Client { Id = OtherIssuerId, CompanyName = "Other", RegistrationNumber = "BT000003", IsIssuer = true, IsActive = true });
            tenantDb.SaveChanges();
        }
        if (!tenantDb.BankAccount.Any(a => a.ClientId == IssuerId))
        {
            tenantDb.BankAccount.Add(new BankAccount { ClientId = IssuerId, AccountNumber = "123456789/0100", IBAN = "CZ6501000000000123456789", IsDefault = true, CurrencyCode = "CZK" });
            tenantDb.BankAccount.Add(new BankAccount { ClientId = OtherIssuerId, AccountNumber = "555/0300", IsDefault = true, CurrencyCode = "CZK" });
            tenantDb.SaveChanges();
        }
    }

    private async Task<HttpClient> AuthClientAsync()
    {
        var client = _factory.CreateClient();
        var login = await AuthHelper.LoginAsSysAdminAsync(client);
        AuthHelper.SetAuthToken(client, login.Token);
        AuthHelper.SetImpersonation(client, CompanyId);
        return client;
    }

    private static CreateInvoiceDto Dto() => new()
    {
        DocumentType = EDocumentType.Invoice, ClientId = CustomerId, IssuerId = IssuerId, CurrencyId = 1,
        InvoiceItem = [new() { OrderIndex = 1, Description = "x", Quantity = 1, Unit = "pcs", UnitPrice = 100 }]
    };

    [Fact]
    public async Task CreateInvoice_WithoutBankData_UsesIssuerDefaultAccount()
    {
        var client = await AuthClientAsync();

        var response = await client.PostAsJsonAsync("/api/invoice", Dto());

        response.StatusCode.ShouldBe(HttpStatusCode.Created, await response.Content.ReadAsStringAsync());
        var invoice = await response.Content.ReadFromJsonAsync<InvoiceDto>();
        invoice!.BankAccountNumber.ShouldBe("123456789/0100");
        invoice.IBAN.ShouldBe("CZ6501000000000123456789");
    }

    [Fact]
    public async Task CreateInvoice_WithForeignBankAccountId_Returns400()
    {
        long foreignId;
        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<TenantDbContext>();
            foreignId = db.BankAccount.First(a => a.ClientId == OtherIssuerId).Id;
        }
        var dto = Dto();
        dto.BankAccountId = foreignId;
        var client = await AuthClientAsync();

        var response = await client.PostAsJsonAsync("/api/invoice", dto);

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task UpdateInvoice_Put_IsPartial_AndReplacesItemsOnlyWhenProvided()
    {
        var client = await AuthClientAsync();
        var created = await (await client.PostAsJsonAsync("/api/invoice", Dto())).Content.ReadFromJsonAsync<InvoiceDto>();

        // Only notes: items, dates and bank data stay untouched.
        var notesOnly = await client.PutAsJsonAsync($"/api/invoice/{created!.Id}", new UpdateInvoiceDto { Notes = "hello" });
        notesOnly.StatusCode.ShouldBe(HttpStatusCode.OK, await notesOnly.Content.ReadAsStringAsync());
        var afterNotes = (await notesOnly.Content.ReadFromJsonAsync<InvoiceDto>())!;
        afterNotes.Notes.ShouldBe("hello");
        afterNotes.InvoiceItem.Count.ShouldBe(1);
        afterNotes.TotalWithVat.ShouldBe(created.TotalWithVat);
        afterNotes.BankAccountNumber.ShouldBe(created.BankAccountNumber);

        // items provided: the whole list is replaced and totals recomputed.
        var replaced = await client.PutAsJsonAsync($"/api/invoice/{created.Id}", new UpdateInvoiceDto
        {
            InvoiceItem =
            [
                new() { OrderIndex = 1, Description = "a", Quantity = 2, Unit = "pcs", UnitPrice = 50 },
                new() { OrderIndex = 2, Description = "b", Quantity = 1, Unit = "pcs", UnitPrice = 30 }
            ]
        });
        replaced.StatusCode.ShouldBe(HttpStatusCode.OK, await replaced.Content.ReadAsStringAsync());
        var afterItems = (await replaced.Content.ReadFromJsonAsync<InvoiceDto>())!;
        afterItems.InvoiceItem.Count.ShouldBe(2);
        afterItems.TotalWithVat.ShouldBe(130m);
        afterItems.Notes.ShouldBe("hello");
    }

    [Fact]
    public async Task UpdateInvoice_Put_UnknownInvoice_Returns404()
    {
        var client = await AuthClientAsync();

        var response = await client.PutAsJsonAsync("/api/invoice/987654321", new UpdateInvoiceDto { Notes = "x" });

        response.StatusCode.ShouldBe(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task RevertToDraft_OnDraft_Returns400()
    {
        var client = await AuthClientAsync();
        var created = await (await client.PostAsJsonAsync("/api/invoice", Dto())).Content.ReadFromJsonAsync<InvoiceDto>();

        var response = await client.PostAsync($"/api/invoice/{created!.Id}/revert-to-draft", null);

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task UpdateInvoice_Put_WithMismatchingExpectedStatus_Returns409_AndChangesNothing()
    {
        var client = await AuthClientAsync();
        var created = await (await client.PostAsJsonAsync("/api/invoice", Dto())).Content.ReadFromJsonAsync<InvoiceDto>();

        var response = await client.PutAsJsonAsync($"/api/invoice/{created!.Id}",
            new UpdateInvoiceDto { Notes = "nope", ExpectedStatus = EInvoiceStatus.Completed });

        response.StatusCode.ShouldBe(HttpStatusCode.Conflict);
        var current = await (await client.GetAsync($"/api/invoice/{created.Id}")).Content.ReadFromJsonAsync<InvoiceDto>();
        current!.Notes.ShouldNotBe("nope");
    }
}

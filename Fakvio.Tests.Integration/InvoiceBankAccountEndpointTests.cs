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
/// a BankAccountId of another company must be rejected with 400.
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
}

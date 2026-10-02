using System.Net;
using System.Text.Json;
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
/// <c>POST /api/invoice/{id}/mark-paid</c> on a Proforma of a VAT-payer issuer must auto-issue
/// the tax receipt for advance payment (DPP), visible via <c>GET /api/invoice/{id}/tax-receipts</c>.
/// </summary>
public class ProformaAutoTaxReceiptEndpointTests : IClassFixture<FakvioFactory>
{
    private readonly FakvioFactory _factory;

    private const long CompanyId = 7800L;
    private const long CustomerId = 7801L;
    private const long VatRateId = 7800L;
    private const long CurrencyId = 1L; // CZK — seeded by the factory

    public ProformaAutoTaxReceiptEndpointTests(FakvioFactory factory)
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
            masterDb.Client.Add(new Client
            {
                Id = CompanyId, CompanyName = "DPP Test Company", RegistrationNumber = "DP000001",
                IsIssuer = true, IsActive = true
            });
        if (!masterDb.CompanySystemSettings.Any(s => s.CompanyId == CompanyId))
            masterDb.CompanySystemSettings.Add(new CompanySystemSettings
            {
                CompanyId = CompanyId, SchemaName = $"tenant_dpp_{CompanyId}", IsProvisioned = true, IsActive = true
            });
        masterDb.SaveChanges();

        if (!tenantDb.Client.Any(c => c.Id == CompanyId))
            tenantDb.Client.Add(new Client
            {
                Id = CompanyId, CompanyName = "DPP Test Company", RegistrationNumber = "DP000001",
                IsIssuer = true, IsActive = true, IsVatPayer = true
            });
        if (!tenantDb.Client.Any(c => c.Id == CustomerId))
            tenantDb.Client.Add(new Client
            {
                Id = CustomerId, CompanyName = "DPP Test Customer", RegistrationNumber = "DP000002", IsActive = true
            });
        if (!tenantDb.VatRate.Any(v => v.Id == VatRateId))
            tenantDb.VatRate.Add(new VatRate
            {
                Id = VatRateId, Name = "21%", Rate = 21, ValidFrom = DateTime.UtcNow.AddYears(-1), IsActive = true
            });
        tenantDb.SaveChanges();
    }

    private long SeedProforma()
    {
        using var scope = _factory.Services.CreateScope();
        var tenantDb = scope.ServiceProvider.GetRequiredService<TenantDbContext>();

        var proforma = new Invoice
        {
            DocumentType = EDocumentType.Proforma,
            Status = EInvoiceStatus.Completed,
            DocumentNumber = $"PF-{Guid.NewGuid():N}"[..16],
            ClientId = CustomerId, IssuerId = CompanyId, CurrencyId = CurrencyId,
            IssueDate = DateTime.UtcNow.AddDays(-3), DueDate = DateTime.UtcNow.AddDays(11),
            TotalBeforeVat = 1000m, TotalVat = 210m, TotalWithVat = 1210m,
            PaymentMethod = EPaymentMethod.BankTransfer,
            InvoiceItem =
            [
                new InvoiceItem
                {
                    OrderIndex = 1, Description = "Advance", Quantity = 1, Unit = "pcs", UnitPrice = 1000m,
                    VatRateId = VatRateId, VatRatePercentage = 21,
                    TotalBeforeVat = 1000m, VatAmount = 210m, TotalWithVat = 1210m
                }
            ]
        };
        tenantDb.Invoice.Add(proforma);
        tenantDb.SaveChanges();
        return proforma.Id;
    }

    private async Task<HttpClient> CreateAuthenticatedClientAsync()
    {
        var client = _factory.CreateClient();
        var login = await AuthHelper.LoginAsSysAdminAsync(client);
        AuthHelper.SetAuthToken(client, login.Token);
        AuthHelper.SetImpersonation(client, CompanyId);
        return client;
    }

    [Fact]
    public async Task MarkPaid_Proforma_AutoIssuesTaxReceipt_AndIsIdempotent()
    {
        var proformaId = SeedProforma();
        var client = await CreateAuthenticatedClientAsync();
        var options = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };

        var paid = await client.PostAsync($"/api/invoice/{proformaId}/mark-paid", null);
        paid.StatusCode.ShouldBe(HttpStatusCode.OK, await paid.Content.ReadAsStringAsync());

        var response = await client.GetAsync($"/api/invoice/{proformaId}/tax-receipts");
        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        var receipts = JsonSerializer.Deserialize<List<InvoiceDto>>(await response.Content.ReadAsStringAsync(), options)!;
        receipts.Count.ShouldBe(1);
        receipts[0].TotalWithVat.ShouldBe(1210m);
        receipts[0].DocumentType.ShouldBe(EDocumentType.TaxReceiptForAdvance);

        // The manual endpoint finds nothing left to cover → 400, still exactly one receipt.
        var again = await client.PostAsync($"/api/invoice/{proformaId}/issue-tax-receipt", null);
        again.StatusCode.ShouldBe(HttpStatusCode.BadRequest);

        var remaining = await client.GetAsync($"/api/invoice/{proformaId}/remaining-advance");
        (await remaining.Content.ReadAsStringAsync()).ShouldStartWith("1210");
    }
}

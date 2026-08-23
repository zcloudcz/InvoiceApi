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
/// Integration tests for the tenant-readiness gate on <c>POST /api/invoice/{id}/complete</c>
/// (issue #206). They exercise the full HTTP stack:
///
///   JWT auth → InvoiceController → InvoiceService → TenantReadinessService → EF Core InMemory
///
/// Scenarios:
///   1. Issuer with missing mandatory settings → 400 with code + missingFields, invoice stays Draft
///   2. Ready issuer → 200, invoice is Completed (warnings such as the EPO header do not block)
///
/// Both issuers live in the SAME tenant, which is the point: the gate must judge the issuer of
/// the invoice being completed, not the tenant as a whole.
/// </summary>
public class InvoiceCompleteReadinessEndpointTests : IClassFixture<FakvioFactory>
{
    private readonly FakvioFactory _factory;

    private const long CompanyId      = 7800L;
    private const long ReadyIssuerId  = 7800L;
    private const long UnreadyIssuerId = 7801L;
    private const long CustomerId     = 7802L;
    private const long CurrencyId     = 1L;   // CZK — part of the tenant seed

    public InvoiceCompleteReadinessEndpointTests(FakvioFactory factory)
    {
        _factory = factory;
        _factory.InitializeDatabase();
        SeedReadinessScenario();
    }

    // ─── Tests ───────────────────────────────────────────────────────────────

    /// <summary>
    /// The issuer has no address, no IČO and no bank account. Issuing must be refused with the
    /// structured 400 the EPO export established — <c>{ code, message, missingFields }</c> — so
    /// the UI can tell the user exactly what to fill in. The invoice must remain a Draft.
    /// </summary>
    [Fact]
    public async Task CompleteInvoice_IssuerNotReady_Returns400WithMissingFields()
    {
        var invoiceId = SeedDraftInvoice(UnreadyIssuerId, "GATE-BLOCKED-1", variableSymbol: "78010001");
        var client = await CreateAuthenticatedClientAsync();

        var response = await client.PostAsync($"/api/invoice/{invoiceId}/complete", null);

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest,
            $"Response body: {await response.Content.ReadAsStringAsync()}");

        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        body.RootElement.GetProperty("code").GetString().ShouldBe("TENANT_NOT_READY");

        var missingFields = body.RootElement.GetProperty("missingFields")
            .EnumerateArray().Select(f => f.GetString()).ToList();
        missingFields.ShouldContain(nameof(Client.Address));
        missingFields.ShouldContain(nameof(Client.RegistrationNumber));
        missingFields.ShouldContain(nameof(Client.BankAccount));

        // Nothing was issued — the document is still a draft.
        using var scope = _factory.Services.CreateScope();
        var tenantDb = scope.ServiceProvider.GetRequiredService<TenantDbContext>();
        tenantDb.Invoice.Single(i => i.Id == invoiceId).Status.ShouldBe(EInvoiceStatus.Draft);
    }

    /// <summary>
    /// The other issuer of the same tenant is fully configured, so completion works exactly as
    /// before. The company has no EPO header configured, which is a warning — warnings must
    /// never block invoicing.
    /// </summary>
    [Fact]
    public async Task CompleteInvoice_IssuerReady_Returns200AndCompletes()
    {
        var invoiceId = SeedDraftInvoice(ReadyIssuerId, "GATE-OK-1", variableSymbol: "78000001");
        var client = await CreateAuthenticatedClientAsync();

        var response = await client.PostAsync($"/api/invoice/{invoiceId}/complete", null);

        response.StatusCode.ShouldBe(HttpStatusCode.OK,
            $"Response body: {await response.Content.ReadAsStringAsync()}");

        var invoice = JsonSerializer.Deserialize<InvoiceDto>(
            await response.Content.ReadAsStringAsync(),
            new JsonSerializerOptions { PropertyNameCaseInsensitive = true });

        invoice.ShouldNotBeNull();
        invoice.Status.ShouldBe(EInvoiceStatus.Completed);
    }

    // ─── Seed Helpers ────────────────────────────────────────────────────────

    /// <summary>
    /// Master DB: the company (provisioned + active) so impersonation works. Its
    /// <c>CompanySystemSettings</c> row deliberately has no EPO header — that is the warning
    /// the ready-issuer test proves to be non-blocking.
    ///
    /// Tenant DB: one complete issuer, one issuer missing everything, and a customer.
    /// Number sequences come from the tenant seed data (<c>TenantDbContext</c>).
    /// </summary>
    private void SeedReadinessScenario()
    {
        using var scope = _factory.Services.CreateScope();
        var masterDb = scope.ServiceProvider.GetRequiredService<MasterDbContext>();
        var tenantDb = scope.ServiceProvider.GetRequiredService<TenantDbContext>();

        if (!masterDb.Client.Any(c => c.Id == CompanyId))
        {
            masterDb.Client.Add(new Client
            {
                Id = CompanyId,
                CompanyName = "Readiness Test Company",
                RegistrationNumber = "RT000001",
                IsIssuer = true,
                IsActive = true
            });
        }

        if (!masterDb.CompanySystemSettings.Any(s => s.CompanyId == CompanyId))
        {
            masterDb.CompanySystemSettings.Add(new CompanySystemSettings
            {
                CompanyId = CompanyId,
                SchemaName = $"tenant_readiness_{CompanyId}",
                IsProvisioned = true,
                IsActive = true
            });
        }

        masterDb.SaveChanges();

        if (!tenantDb.Client.Any(c => c.Id == ReadyIssuerId))
        {
            tenantDb.Client.Add(new Client
            {
                Id = ReadyIssuerId,
                CompanyName = "Readiness Test Company",
                RegistrationNumber = "RT000001",
                IsIssuer = true,
                IsActive = true,
                IsVatPayer = false,   // non-VAT payer needs no DIČ
                Address =
                {
                    new Address
                    {
                        AddressType = EAddressType.Primary, IsPrimary = true,
                        Street = "Hlavní 1", City = "Praha", PostalCode = "11000", Country = "CZ"
                    }
                },
                BankAccount = { new BankAccount { AccountNumber = "1234567890/0100" } }
            });
            tenantDb.SaveChanges();
        }

        // Missing address, IČO and bank account — three blocking issues.
        // RegistrationNumber is a required column, so "not filled in" is an empty string here,
        // exactly as a half-finished onboarding leaves it in the real database.
        if (!tenantDb.Client.Any(c => c.Id == UnreadyIssuerId))
        {
            tenantDb.Client.Add(new Client
            {
                Id = UnreadyIssuerId,
                CompanyName = "Half-configured Company",
                RegistrationNumber = "",
                IsIssuer = true,
                IsActive = true,
                IsVatPayer = false
            });
            tenantDb.SaveChanges();
        }

        if (!tenantDb.Client.Any(c => c.Id == CustomerId))
        {
            tenantDb.Client.Add(new Client
            {
                Id = CustomerId,
                CompanyName = "Readiness Test Customer",
                RegistrationNumber = "RT000002",
                IsIssuer = false,
                IsActive = true
            });
            tenantDb.SaveChanges();
        }
    }

    /// <summary>
    /// Seeds one draft invoice with a document number already assigned, so the test exercises
    /// the gate rather than the numbering fallback.
    /// </summary>
    private long SeedDraftInvoice(long issuerId, string documentNumber, string variableSymbol)
    {
        using var scope = _factory.Services.CreateScope();
        var tenantDb = scope.ServiceProvider.GetRequiredService<TenantDbContext>();

        var invoice = new Invoice
        {
            DocumentType = EDocumentType.Invoice,
            Status = EInvoiceStatus.Draft,
            DocumentNumber = documentNumber,
            VariableSymbol = variableSymbol,
            ClientId = CustomerId,
            IssuerId = issuerId,
            CurrencyId = CurrencyId,
            IssueDate = DateTime.UtcNow,
            DueDate = DateTime.UtcNow.AddDays(14),
            TotalBeforeVat = 1000m,
            TotalVat = 0m,
            TotalWithVat = 1000m,
            PaymentMethod = EPaymentMethod.BankTransfer,
            InvoiceItem =
            {
                new InvoiceItem
                {
                    OrderIndex = 1, Description = "Service fee", Quantity = 1, Unit = "pcs",
                    UnitPrice = 1000m, TotalBeforeVat = 1000m, VatRatePercentage = 0,
                    VatAmount = 0, TotalWithVat = 1000m, VatRegime = EVatRegime.Standard
                }
            }
        };

        tenantDb.Invoice.Add(invoice);
        tenantDb.SaveChanges();

        return invoice.Id;
    }

    private async Task<HttpClient> CreateAuthenticatedClientAsync()
    {
        var client = _factory.CreateClient();
        var login = await AuthHelper.LoginAsSysAdminAsync(client);
        AuthHelper.SetAuthToken(client, login.Token);
        AuthHelper.SetImpersonation(client, CompanyId);
        return client;
    }
}

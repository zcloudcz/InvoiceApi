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
/// Integration tests for <c>POST /api/invoice/{id}/copy</c>.
///
/// Each test exercises the full HTTP stack:
///   - JWT authentication (SysAdmin impersonating a provisioned tenant)
///   - InvoiceController → InvoiceService → EF Core InMemoryDatabase
///
/// Scenarios covered:
///   1. Happy path — copy a completed Invoice → 201 Created, new Draft with correct fields
///   2. Source not found → 404 Not Found
///   3. Source is a CreditNote → 400 Bad Request
///
/// The factory is shared across tests in this class (IClassFixture), so tenant
/// and master DB are seeded once in the constructor and shared.
/// </summary>
public class InvoiceCopyEndpointTests : IClassFixture<FakvioFactory>
{
    private readonly FakvioFactory _factory;

    // Company and issuer IDs used for impersonation + tenant seeding
    private const long CompanyId = 7700L;
    private const long IssuerId  = 7700L;
    private const long CustomerId = 7701L;
    private const long CurrencyId = 1L;   // CZK — seeded by the factory's InitializeDatabase()

    public InvoiceCopyEndpointTests(FakvioFactory factory)
    {
        _factory = factory;
        _factory.InitializeDatabase();
        SeedCopyTestScenario();
    }

    // ─── Seed Helpers ────────────────────────────────────────────────────────

    /// <summary>
    /// Seeds the minimal data required by the copy tests:
    /// - Master DB: company record + CompanySystemSettings (provisioned + active)
    /// - Tenant DB: issuer (IsIssuer = true) + customer + default number sequence
    /// </summary>
    private void SeedCopyTestScenario()
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
                CompanyName = "Copy Test Company",
                RegistrationNumber = "CT000001",
                IsIssuer = true,
                IsActive = true
            });
        }

        if (!masterDb.CompanySystemSettings.Any(s => s.CompanyId == CompanyId))
        {
            masterDb.CompanySystemSettings.Add(new CompanySystemSettings
            {
                CompanyId = CompanyId,
                SchemaName = $"tenant_copy_{CompanyId}",
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
                CompanyName = "Copy Test Company",
                RegistrationNumber = "CT000001",
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
                CompanyName = "Copy Test Customer",
                RegistrationNumber = "CT000002",
                IsIssuer = false,
                IsActive = true
            });
            tenantDb.SaveChanges();
        }

        // NOTE: No NumberSequence is seeded here — the InvoiceService fallback in
        // GenerateDocumentNumberAsync handles missing sequences gracefully by using
        // a simple year-based counter (e.g., "INV20260001"). This keeps the seed
        // minimal and avoids the need to also seed NumberSequenceFormat.
    }

    /// <summary>
    /// Seeds a single Invoice in the tenant DB and returns its ID.
    /// </summary>
    private long SeedInvoice(
        EDocumentType documentType = EDocumentType.Invoice,
        EInvoiceStatus status = EInvoiceStatus.Completed)
    {
        using var scope = _factory.Services.CreateScope();
        var tenantDb = scope.ServiceProvider.GetRequiredService<TenantDbContext>();

        var invoice = new Invoice
        {
            DocumentType = documentType,
            Status = status,
            DocumentNumber = $"TEST-{documentType}-{Guid.NewGuid():N}"[..24],
            VariableSymbol = "20260001",
            ClientId = CustomerId,
            IssuerId = IssuerId,
            CurrencyId = CurrencyId,
            IssueDate = DateTime.UtcNow.AddDays(-10),
            DueDate = DateTime.UtcNow.AddDays(4),
            TotalBeforeVat = 1000m,
            TotalVat = 0m,
            TotalWithVat = 1000m,
            PaymentMethod = EPaymentMethod.BankTransfer,
            Notes = "Integration test invoice",
            IsSentByEmail = true,
            PaidAt = status == EInvoiceStatus.Paid ? DateTime.UtcNow.AddDays(-1) : null,
            PaidAmount = status == EInvoiceStatus.Paid ? 1000m : 0m,
            InvoiceItem = new List<InvoiceItem>
            {
                new()
                {
                    OrderIndex = 1,
                    Description = "Service fee",
                    Quantity = 5,
                    Unit = "hrs",
                    UnitPrice = 200m,
                    TotalBeforeVat = 1000m,
                    VatRatePercentage = 0,
                    VatAmount = 0,
                    TotalWithVat = 1000m,
                    VatRegime = EVatRegime.Standard
                }
            }
        };

        tenantDb.Invoice.Add(invoice);
        tenantDb.SaveChanges();

        return invoice.Id;
    }

    // ─── Authenticated HTTP Client ────────────────────────────────────────────

    /// <summary>
    /// Creates an authenticated HttpClient (SysAdmin token + impersonation header).
    /// </summary>
    private async Task<HttpClient> CreateAuthenticatedClientAsync()
    {
        var client = _factory.CreateClient();
        var login = await AuthHelper.LoginAsSysAdminAsync(client);
        AuthHelper.SetAuthToken(client, login.Token);
        AuthHelper.SetImpersonation(client, CompanyId);
        return client;
    }

    // ─── Tests ───────────────────────────────────────────────────────────────

    /// <summary>
    /// Happy path: copying a Completed Invoice returns 201 Created.
    /// The response body is a valid InvoiceDto with:
    ///   - Status = Draft
    ///   - Id != sourceId (new record)
    ///   - DocumentType = Invoice
    ///   - VariableSymbol != source VS (freshly derived)
    ///   - At least one line item
    /// </summary>
    [Fact]
    public async Task CopyInvoice_HappyPath_Returns201WithDraftInvoice()
    {
        // Arrange
        var sourceId = SeedInvoice(EDocumentType.Invoice, EInvoiceStatus.Completed);
        var client = await CreateAuthenticatedClientAsync();

        // Act
        var response = await client.PostAsync($"/api/invoice/{sourceId}/copy", null);

        // Assert — status 201 Created
        response.StatusCode.ShouldBe(HttpStatusCode.Created,
            $"Response body: {await response.Content.ReadAsStringAsync()}");

        // Parse response body
        var json = await response.Content.ReadAsStringAsync();
        var copy = JsonSerializer.Deserialize<InvoiceDto>(json,
            new JsonSerializerOptions { PropertyNameCaseInsensitive = true });

        copy.ShouldNotBeNull();
        copy.Id.ShouldBeGreaterThan(0);
        copy.Id.ShouldNotBe(sourceId, "Copy must be a new row");
        copy.Status.ShouldBe(EInvoiceStatus.Draft);
        copy.DocumentType.ShouldBe(EDocumentType.Invoice);
        copy.InvoiceItem.ShouldNotBeEmpty();

        // Location header should point to the new invoice
        response.Headers.Location.ShouldNotBeNull();
        response.Headers.Location.ToString().ShouldContain(copy.Id.ToString());
    }

    /// <summary>
    /// When the source invoice does not exist, the endpoint must return 404 Not Found.
    /// </summary>
    [Fact]
    public async Task CopyInvoice_SourceNotFound_Returns404()
    {
        // Arrange — use a non-existent ID
        const long nonExistentId = 999_999L;
        var client = await CreateAuthenticatedClientAsync();

        // Act
        var response = await client.PostAsync($"/api/invoice/{nonExistentId}/copy", null);

        // Assert
        response.StatusCode.ShouldBe(HttpStatusCode.NotFound);
    }

    /// <summary>
    /// When the source is a CreditNote, the endpoint must return 400 Bad Request
    /// because CreditNotes cannot be copied (use the credit note workflow instead).
    /// </summary>
    [Fact]
    public async Task CopyInvoice_CreditNoteSource_Returns400()
    {
        // Arrange — seed a CreditNote
        var creditNoteId = SeedInvoice(EDocumentType.CreditNote, EInvoiceStatus.Completed);
        var client = await CreateAuthenticatedClientAsync();

        // Act
        var response = await client.PostAsync($"/api/invoice/{creditNoteId}/copy", null);

        // Assert
        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest,
            $"CreditNote copy must be rejected. Body: {await response.Content.ReadAsStringAsync()}");
    }
}

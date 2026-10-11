using System.Net;
using System.Net.Http.Json;
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
/// HTTP contract of the UNIFIED document API (<c>api/invoice</c> serves every document type, DEVGUIDE §5.6):
/// create / get / list scope / update / every transition / bulk for a RECEIVED document, and the 400 business error
/// of the issued-only operations. The data is seeded through the factory's own scope, the requests go through the
/// full pipeline (JWT, tenant middleware, controller filters, EF).
/// </summary>
public class UnifiedDocumentEndpointTests : IClassFixture<FakvioFactory>
{
    private readonly FakvioFactory _factory;

    private const long CompanyId = 9100L; // also the id of our company inside the tenant (the IsIssuer client)
    private const long SupplierId = 9101L;
    private const long CurrencyId = 1L; // CZK seeded by InitializeDatabase()

    public UnifiedDocumentEndpointTests(FakvioFactory factory)
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
                Id = CompanyId, CompanyName = "Unified Test Co", RegistrationNumber = "UD000001", IsIssuer = true, IsActive = true
            });
        if (!masterDb.CompanySystemSettings.Any(s => s.CompanyId == CompanyId))
            masterDb.CompanySystemSettings.Add(new CompanySystemSettings
            {
                CompanyId = CompanyId, SchemaName = $"tenant_ud_{CompanyId}", IsProvisioned = true, IsActive = true
            });
        masterDb.SaveChanges();

        // Our own company inside the tenant (what a received document gets as IssuerId) and a supplier.
        if (!tenantDb.Client.Any(c => c.Id == CompanyId))
            tenantDb.Client.Add(new Client
            {
                Id = CompanyId, CompanyName = "Unified Test Co", RegistrationNumber = "UD000001", IsIssuer = true, IsActive = true
            });
        if (!tenantDb.Client.Any(c => c.Id == SupplierId))
            tenantDb.Client.Add(new Client
            {
                Id = SupplierId, CompanyName = "Unified Supplier", RegistrationNumber = "UD000002", IsIssuer = false, IsActive = true
            });
        tenantDb.SaveChanges();
    }

    private long SeedDoc(EDocumentType type, EInvoiceStatus status, decimal paidAmount = 0m, string? variableSymbol = null)
    {
        using var scope = _factory.Services.CreateScope();
        var tenantDb = scope.ServiceProvider.GetRequiredService<TenantDbContext>();

        var invoice = new Invoice
        {
            DocumentNumber = $"UD-{Guid.NewGuid():N}"[..14],
            DocumentType = type, Status = status, ClientId = SupplierId, IssuerId = CompanyId, CurrencyId = CurrencyId,
            VariableSymbol = variableSymbol ?? Random.Shared.NextInt64(1_000_000_000, 9_999_999_999).ToString(),
            IssueDate = DateTime.UtcNow.AddDays(-5), DueDate = DateTime.UtcNow.AddDays(10), TaxableSupplyDate = DateTime.UtcNow.AddDays(-5),
            ReceivedDate = type == EDocumentType.ReceivedInvoice ? DateTime.UtcNow : null,
            TotalBeforeVat = 100m, TotalVat = 21m, TotalWithVat = 121m, PaidAmount = paidAmount,
            PaidAt = paidAmount > 0 ? DateTime.UtcNow : null
        };
        tenantDb.Invoice.Add(invoice);
        tenantDb.SaveChanges();
        return invoice.Id;
    }

    private (EInvoiceStatus Status, string? Number, string? Notes) RowOf(long id)
    {
        using var scope = _factory.Services.CreateScope();
        var row = scope.ServiceProvider.GetRequiredService<TenantDbContext>().Invoice.Single(i => i.Id == id);
        return (row.Status, row.DocumentNumber, row.Notes);
    }

    /// <summary>Adds one FileAttachment metadata row to a received document (the list only counts rows).</summary>
    private void SeedReceivedAttachment(long recordId, string fileName)
    {
        using var scope = _factory.Services.CreateScope();
        var tenantDb = scope.ServiceProvider.GetRequiredService<TenantDbContext>();
        tenantDb.FileAttachment.Add(new FileAttachment
        {
            EntityName = "ReceivedInvoice",
            RecordId = recordId,
            OriginalFileName = fileName,
            ContentType = "application/pdf",
            FileSizeBytes = 1,
            BlobPath = $"{CompanyId}/{Guid.NewGuid()}.pdf"
        });
        tenantDb.SaveChanges();
    }

    private async Task<HttpClient> CreateAuthenticatedClientAsync()
    {
        var client = _factory.CreateClient();
        var login = await AuthHelper.LoginAsSysAdminAsync(client);
        AuthHelper.SetAuthToken(client, login.Token);
        AuthHelper.SetImpersonation(client, CompanyId);
        return client;
    }

    private static async Task<JsonElement> JsonOf(HttpResponseMessage response)
    {
        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return doc.RootElement.Clone();
    }

    private static object NewReceivedBody(string number) => new
    {
        documentType = (int)EDocumentType.ReceivedInvoice,
        clientId = SupplierId,
        issuerId = 0, // ignored for a received document: the server sets our company
        currencyId = CurrencyId,
        customDocumentNumber = number,
        issueDate = "2026-05-02T00:00:00Z",
        receivedDate = "2026-05-04T00:00:00Z",
        invoiceItem = new[] { new { description = "Hosting", quantity = 2, unit = "ks", unitPrice = 100, vatRatePercentage = 21 } }
    };

    // ─── Create / get / lists / update ───────────────────────────────────────

    [Fact]
    public async Task ReceivedDocument_CanBeCreated_Read_Listed_AndUpdated_ThroughTheUnifiedApi()
    {
        var client = await CreateAuthenticatedClientAsync();
        var number = $"SUP-{Guid.NewGuid():N}"[..12];

        var create = await client.PostAsJsonAsync("/api/invoice", NewReceivedBody(number));
        create.StatusCode.ShouldBe(HttpStatusCode.Created);
        var created = await JsonOf(create);
        var id = created.GetProperty("id").GetInt64();
        created.GetProperty("documentNumber").GetString().ShouldBe(number);
        created.GetProperty("clientId").GetInt64().ShouldBe(SupplierId);
        created.GetProperty("issuerId").GetInt64().ShouldBe(CompanyId);
        created.GetProperty("totalWithVat").GetDecimal().ShouldBe(242m);

        // Read through the unified API, through the legacy API, and by number with an explicit scope.
        (await client.GetAsync($"/api/invoice/{id}")).StatusCode.ShouldBe(HttpStatusCode.OK);
        (await client.GetAsync($"/api/received-invoice/{id}")).StatusCode.ShouldBe(HttpStatusCode.OK);
        (await client.GetAsync($"/api/invoice/by-number/{number}")).StatusCode.ShouldBe(HttpStatusCode.NotFound); // default scope = issued
        (await client.GetAsync($"/api/invoice/by-number/{number}?documentType={(int)EDocumentType.ReceivedInvoice}"))
            .StatusCode.ShouldBe(HttpStatusCode.OK);

        // List scope: issued by default, received on request.
        var defaultList = await JsonOf(await client.GetAsync("/api/invoice/paged?pageSize=100"));
        defaultList.GetProperty("items").EnumerateArray().Select(i => i.GetProperty("id").GetInt64()).ShouldNotContain(id);
        var receivedList = await JsonOf(await client.GetAsync($"/api/invoice/paged?pageSize=100&DocumentTypes={(int)EDocumentType.ReceivedInvoice}"));
        var rows = receivedList.GetProperty("items").EnumerateArray().ToList();
        rows.Select(i => i.GetProperty("id").GetInt64()).ShouldContain(id);
        rows.ShouldAllBe(r => r.GetProperty("documentType").GetInt32() == (int)EDocumentType.ReceivedInvoice);
        var plain = await JsonOf(await client.GetAsync($"/api/invoice?documentType={(int)EDocumentType.ReceivedInvoice}"));
        plain.EnumerateArray().Select(i => i.GetProperty("id").GetInt64()).ShouldContain(id);

        // Update: supplier number, received date; the number is the caller's, not a sequence.
        var update = await client.PutAsJsonAsync($"/api/invoice/{id}", new { documentNumber = number + "B", receivedDate = "2026-06-01T00:00:00Z" });
        update.StatusCode.ShouldBe(HttpStatusCode.OK);
        RowOf(id).Number.ShouldBe(number + "B");
    }

    [Fact]
    public async Task CreateReceived_WithUnknownSupplier_Returns400()
    {
        var client = await CreateAuthenticatedClientAsync();
        var response = await client.PostAsJsonAsync("/api/invoice", new
        {
            documentType = 5, clientId = 99999, issuerId = 0, currencyId = CurrencyId, customDocumentNumber = "X-1",
            invoiceItem = new[] { new { description = "Hosting", quantity = 1, unitPrice = 1, vatRatePercentage = 21 } }
        });

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await JsonOf(response)).GetProperty("message").GetString()!.ShouldContain("Supplier");
    }

    // ─── Variable symbol: free text for a received invoice, "digits, max 10" for an issued one ───

    private const string SupplierSymbol = "INV-2026-0042"; // not numeric and longer than 10: legal on a supplier's invoice

    private string? VariableSymbolOf(long id)
    {
        using var scope = _factory.Services.CreateScope();
        return scope.ServiceProvider.GetRequiredService<TenantDbContext>().Invoice.Single(i => i.Id == id).VariableSymbol;
    }

    /// <summary>
    /// A form round-trips every field on save. A received invoice stored with the supplier's own symbol must stay
    /// saveable when the user changed only the note (PR #537 review: the issued "max 10 digits" rule blocked it).
    /// </summary>
    [Fact]
    public async Task ReceivedDocument_WithSuppliersNonNumericVariableSymbol_CanBeUpdatedAndCreated()
    {
        var id = SeedDoc(EDocumentType.ReceivedInvoice, EInvoiceStatus.Draft, variableSymbol: SupplierSymbol);
        var client = await CreateAuthenticatedClientAsync();

        var update = await client.PutAsJsonAsync($"/api/invoice/{id}", new { variableSymbol = SupplierSymbol, notes = "only the note changed" });

        update.StatusCode.ShouldBe(HttpStatusCode.OK);
        RowOf(id).Notes.ShouldBe("only the note changed");
        VariableSymbolOf(id).ShouldBe(SupplierSymbol);

        var create = await client.PostAsJsonAsync("/api/invoice", new
        {
            documentType = (int)EDocumentType.ReceivedInvoice, clientId = SupplierId, issuerId = 0, currencyId = CurrencyId,
            customDocumentNumber = $"SUP-{Guid.NewGuid():N}"[..12], variableSymbol = SupplierSymbol,
            invoiceItem = new[] { new { description = "Hosting", quantity = 1, unit = "ks", unitPrice = 100, vatRatePercentage = 21 } }
        });

        create.StatusCode.ShouldBe(HttpStatusCode.Created);
        (await JsonOf(create)).GetProperty("variableSymbol").GetString().ShouldBe(SupplierSymbol);
    }

    /// <summary>Issued documents keep the old rule, the old message and the old validation-400 shape.</summary>
    [Theory]
    [InlineData("INV-2026-0042")] // not digits
    [InlineData("12345678901")]   // 11 digits
    public async Task IssuedDocument_RefusesAVariableSymbolThatIsNotMax10Digits_OnCreateAndUpdate(string symbol)
    {
        var id = SeedDoc(EDocumentType.Invoice, EInvoiceStatus.Draft);
        var before = VariableSymbolOf(id);
        var client = await CreateAuthenticatedClientAsync();

        var update = await client.PutAsJsonAsync($"/api/invoice/{id}", new { variableSymbol = symbol });
        var create = await client.PostAsJsonAsync("/api/invoice", new
        {
            documentType = (int)EDocumentType.Invoice, clientId = SupplierId, issuerId = CompanyId, currencyId = CurrencyId,
            variableSymbol = symbol, variableSymbolIsManualOverride = true,
            invoiceItem = new[] { new { description = "Hosting", quantity = 1, unit = "ks", unitPrice = 100, vatRatePercentage = 21 } }
        });

        foreach (var response in new[] { update, create })
        {
            response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
            (await JsonOf(response)).GetProperty("errors").GetProperty("VariableSymbol")[0].GetString()
                .ShouldBe("Variable symbol must contain only digits (max 10)");
        }
        VariableSymbolOf(id).ShouldBe(before);
    }

    // ─── Transitions ─────────────────────────────────────────────────────────

    [Fact]
    public async Task ReceivedDocument_RunsEveryTransition_ThroughTheUnifiedRoutes()
    {
        var id = SeedDoc(EDocumentType.ReceivedInvoice, EInvoiceStatus.Paid, paidAmount: 121m);
        var client = await CreateAuthenticatedClientAsync();

        (await client.PostAsync($"/api/invoice/{id}/mark-unpaid", null)).StatusCode.ShouldBe(HttpStatusCode.OK);
        RowOf(id).Status.ShouldBe(EInvoiceStatus.Completed);
        (await client.PostAsync($"/api/invoice/{id}/revert-to-draft", null)).StatusCode.ShouldBe(HttpStatusCode.OK);
        RowOf(id).Status.ShouldBe(EInvoiceStatus.Draft);
        (await client.DeleteAsync($"/api/invoice/{id}")).StatusCode.ShouldBe(HttpStatusCode.NoContent);
        RowOf(id).Status.ShouldBe(EInvoiceStatus.Deleted);
        (await client.PostAsync($"/api/invoice/{id}/restore", null)).StatusCode.ShouldBe(HttpStatusCode.OK);
        (await client.PostAsync($"/api/invoice/{id}/complete", null)).StatusCode.ShouldBe(HttpStatusCode.OK); // "approve"
        RowOf(id).Status.ShouldBe(EInvoiceStatus.Completed);
        (await client.PostAsync($"/api/invoice/{id}/mark-paid", null)).StatusCode.ShouldBe(HttpStatusCode.OK);
        RowOf(id).Status.ShouldBe(EInvoiceStatus.Paid);

        // The supplier's number is never touched by any of it.
        RowOf(id).Number.ShouldStartWith("UD-");
    }

    [Fact]
    public async Task ForbiddenTransitions_OnAReceivedDocument_Return400_AndChangeNothing()
    {
        var paid = SeedDoc(EDocumentType.ReceivedInvoice, EInvoiceStatus.Paid, paidAmount: 121m);
        var client = await CreateAuthenticatedClientAsync();

        (await client.PostAsync($"/api/invoice/{paid}/revert-to-draft", null)).StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await client.DeleteAsync($"/api/invoice/{paid}")).StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await client.PutAsJsonAsync($"/api/invoice/{paid}", new { notes = "x" })).StatusCode.ShouldBe(HttpStatusCode.BadRequest);

        RowOf(paid).Status.ShouldBe(EInvoiceStatus.Paid);
        RowOf(paid).Notes.ShouldBeNull();
    }

    [Fact]
    public async Task BulkOperations_AcceptIdsOfAnyType()
    {
        var received = SeedDoc(EDocumentType.ReceivedInvoice, EInvoiceStatus.Completed);
        var issued = SeedDoc(EDocumentType.Invoice, EInvoiceStatus.Completed);
        var client = await CreateAuthenticatedClientAsync();

        var response = await client.PostAsJsonAsync("/api/invoice/bulk/mark-paid", new { invoiceIds = new[] { received, issued } });

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        var result = await JsonOf(response);
        result.GetProperty("successCount").GetInt32().ShouldBe(2);
        RowOf(received).Status.ShouldBe(EInvoiceStatus.Paid);
        RowOf(issued).Status.ShouldBe(EInvoiceStatus.Paid);
    }

    // The unified grid (Invoices.razor on /received-invoices, #542) drives a received list through these bulk routes.

    [Fact]
    public async Task BulkComplete_ApprovesDraftReceivedDocuments()
    {
        var draft = SeedDoc(EDocumentType.ReceivedInvoice, EInvoiceStatus.Draft);
        var client = await CreateAuthenticatedClientAsync();

        var response = await client.PostAsJsonAsync("/api/invoice/bulk/complete", new { invoiceIds = new[] { draft } });

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        (await JsonOf(response)).GetProperty("successCount").GetInt32().ShouldBe(1);
        RowOf(draft).Status.ShouldBe(EInvoiceStatus.Completed); // "approved"
    }

    [Fact]
    public async Task BulkDelete_DeletesDraftReceivedDocuments_AndReportsTheOnesThatCannotBeDeleted()
    {
        var draft = SeedDoc(EDocumentType.ReceivedInvoice, EInvoiceStatus.Draft);
        var completed = SeedDoc(EDocumentType.ReceivedInvoice, EInvoiceStatus.Completed); // a received document needs a revert first
        var client = await CreateAuthenticatedClientAsync();

        var response = await client.PostAsJsonAsync("/api/invoice/bulk/delete", new { invoiceIds = new[] { draft, completed } });

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        var result = await JsonOf(response);
        result.GetProperty("successCount").GetInt32().ShouldBe(1);
        result.GetProperty("failedCount").GetInt32().ShouldBe(1);
        RowOf(draft).Status.ShouldBe(EInvoiceStatus.Deleted);
        RowOf(completed).Status.ShouldBe(EInvoiceStatus.Completed);
    }

    [Fact]
    public async Task PagedList_WithDocumentTypeReceivedInvoice_ReturnsReceivedRowsWithAttachmentCount()
    {
        var withFiles = SeedDoc(EDocumentType.ReceivedInvoice, EInvoiceStatus.Draft);
        var withoutFiles = SeedDoc(EDocumentType.ReceivedInvoice, EInvoiceStatus.Draft);
        var issued = SeedDoc(EDocumentType.Invoice, EInvoiceStatus.Draft);
        SeedReceivedAttachment(withFiles, "a.pdf");
        SeedReceivedAttachment(withFiles, "b.pdf");
        var client = await CreateAuthenticatedClientAsync();

        // The grid sends the singular "DocumentType" filter by name.
        var list = await JsonOf(await client.GetAsync("/api/invoice/paged?pageSize=100&DocumentType=ReceivedInvoice"));

        var rows = list.GetProperty("items").EnumerateArray().ToDictionary(r => r.GetProperty("id").GetInt64());
        rows.ShouldContainKey(withFiles);
        rows.ShouldContainKey(withoutFiles);
        rows.ShouldNotContainKey(issued);
        rows[withFiles].GetProperty("attachmentCount").GetInt32().ShouldBe(2);
        rows[withoutFiles].GetProperty("attachmentCount").GetInt32().ShouldBe(0);
    }

    [Fact]
    public async Task PaymentsOfADocument_TakeTheSideFromTheStoredDocument()
    {
        var received = SeedDoc(EDocumentType.ReceivedInvoice, EInvoiceStatus.Completed);
        var issued = SeedDoc(EDocumentType.Invoice, EInvoiceStatus.Completed);
        var client = await CreateAuthenticatedClientAsync();

        (await client.GetAsync($"/api/invoice/{received}/payments")).StatusCode.ShouldBe(HttpStatusCode.OK);
        (await client.GetAsync($"/api/invoice/{issued}/payments")).StatusCode.ShouldBe(HttpStatusCode.OK);
        (await client.GetAsync("/api/invoice/987654/payments")).StatusCode.ShouldBe(HttpStatusCode.NotFound);
        (await client.PostAsync($"/api/invoice/{received}/auto-match", null)).StatusCode.ShouldBeOneOf(HttpStatusCode.OK, HttpStatusCode.NoContent);
    }

    // ─── Issued-only operations: 400 with a business message ─────────────────

    [Theory]
    [InlineData("GET", "{id}/pdf")]
    [InlineData("GET", "{id}/isdoc")]
    [InlineData("POST", "{id}/send-email")]
    [InlineData("GET", "{id}/email-defaults")]
    [InlineData("GET", "{id}/qr")]
    [InlineData("GET", "{id}/qr/sind")]
    [InlineData("GET", "{id}/qr/spd")]
    [InlineData("GET", "{id}/ubl")]
    [InlineData("POST", "{id}/credit-note")]
    [InlineData("GET", "{id}/credit-notes")]
    [InlineData("POST", "{id}/issue-final")]
    [InlineData("GET", "{id}/remaining-advance")]
    [InlineData("GET", "{id}/final-invoices")]
    [InlineData("POST", "{id}/issue-tax-receipt")]
    [InlineData("GET", "{id}/tax-receipts")]
    [InlineData("GET", "bulk/pdf?ids={id}")]
    [InlineData("GET", "bulk/isdoc?ids={id}")]
    [InlineData("GET", "bulk/ubl?ids={id}")]
    public async Task IssuedOnlyOperation_GivenReceivedDocument_Returns400WithBusinessMessage(string method, string route)
    {
        var id = SeedDoc(EDocumentType.ReceivedInvoice, EInvoiceStatus.Completed);
        var client = await CreateAuthenticatedClientAsync();
        var url = "/api/invoice/" + route.Replace("{id}", id.ToString());

        // A valid body for the POSTs, so that a 400 can only come from the guard and not from model validation.
        var body = new
        {
            documentType = 2, clientId = SupplierId, issuerId = CompanyId, currencyId = CurrencyId,
            recipientEmail = "billing@example.com", invoiceItem = new[] { new { description = "x", quantity = 1, unitPrice = 1, vatRatePercentage = 21 } }
        };
        var response = method == "GET" ? await client.GetAsync(url) : await client.PostAsJsonAsync(url, body);

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await JsonOf(response)).GetProperty("message").GetString()!.ShouldContain("received invoice");
        RowOf(id).Status.ShouldBe(EInvoiceStatus.Completed);
    }

    /// <summary>
    /// Bulk exports: one received id anywhere in the list refuses the whole request (and names that document).
    /// More than 100 ids are NOT refused here (the grid selects across pages); only download links are capped.
    /// </summary>
    [Theory]
    [InlineData("bulk/pdf")]
    [InlineData("bulk/isdoc")]
    [InlineData("bulk/ubl")]
    public async Task BulkExport_RefusesAMixedList_ButNotMoreThan100Ids(string route)
    {
        var issued = SeedDoc(EDocumentType.Invoice, EInvoiceStatus.Completed);
        var received = SeedDoc(EDocumentType.ReceivedInvoice, EInvoiceStatus.Completed);
        var client = await CreateAuthenticatedClientAsync();

        var mixed = await client.GetAsync($"/api/invoice/{route}?ids={issued},{received}");
        var many = await client.GetAsync($"/api/invoice/{route}?ids={string.Join(',', Enumerable.Repeat(issued, 1).Concat(Enumerable.Range(900001, 100).Select(i => (long)i)))}");

        mixed.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await JsonOf(mixed)).GetProperty("message").GetString()!.ShouldContain($"Document {received} is a received invoice");
        // 101 ids (one real, the rest unknown): the guard must not answer "too many".
        if (many.StatusCode == HttpStatusCode.BadRequest)
            (await many.Content.ReadAsStringAsync()).ShouldNotContain("maximum is");
    }

    [Fact]
    public async Task IssuedOnlyOperation_GivenIssuedDocument_IsNotRefusedByTheGuard()
    {
        var id = SeedDoc(EDocumentType.Invoice, EInvoiceStatus.Completed);
        var client = await CreateAuthenticatedClientAsync();

        var response = await client.GetAsync($"/api/invoice/{id}/credit-notes");

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
    }
}

using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;

namespace Fakvio.Tests.Playwright.Infrastructure;

/// <summary>
/// Creates the test data a browser test needs through the REST API, so a test does not depend on what
/// happens to be in the dev database. Works as the seeded SysAdmin impersonating company 1 - the same company
/// <see cref="FakvioPageTest"/> impersonates in the browser.
/// </summary>
public static class ApiSeed
{
    private const string CompanyId = "1";

    private static async Task<HttpClient> OpenAsync(TestConfiguration config)
    {
        // Seeding CREATES and ISSUES documents (an issued invoice takes a number from the real sequence).
        // That is only acceptable on a developer's local stack, never on a shared or production API.
        if (!new Uri(config.ApiUrl).IsLoopback)
            throw new InvalidOperationException(
                $"Test data seeding is allowed against a local API only, not {config.ApiUrl}.");

        var token = await AuthHelper.GetTokenAsync(config.ApiUrl, config.AdminEmail, config.AdminPassword);
        var http = new HttpClient { BaseAddress = new Uri(config.ApiUrl) };
        http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        http.DefaultRequestHeaders.Add("X-Company-Id", CompanyId);
        return http;
    }

    private static async Task<JsonElement> ReadAsync(HttpResponseMessage response)
    {
        var text = await response.Content.ReadAsStringAsync();
        if (!response.IsSuccessStatusCode)
            throw new InvalidOperationException($"Seeding failed: {(int)response.StatusCode} {text}");
        using var doc = JsonDocument.Parse(text);
        return doc.RootElement.Clone();
    }

    /// <summary>Creates a plain client (customer/supplier) and returns its id.</summary>
    public static async Task<long> CreateClientAsync(TestConfiguration config, string companyName)
    {
        using var http = await OpenAsync(config);
        var created = await ReadAsync(await http.PostAsJsonAsync("api/client", new
        {
            companyName,
            // The database requires a registration number; a random 8-digit one keeps seeded clients apart.
            registrationNumber = Random.Shared.Next(10_000_000, 99_999_999).ToString(),
            fetchFromAres = false
        }));
        return created.GetProperty("id").GetInt64();
    }

    /// <summary>Creates a received invoice (a Draft, "Přijato") with one item and returns its id.</summary>
    public static async Task<long> CreateReceivedInvoiceAsync(
        TestConfiguration config, long supplierId, string documentNumber, decimal vatPercentage = 21m)
    {
        using var http = await OpenAsync(config);
        var currencies = await ReadAsync(await http.GetAsync("api/currency/active"));
        var currencyId = currencies.EnumerateArray().First().GetProperty("id").GetInt64();

        var created = await ReadAsync(await http.PostAsJsonAsync("api/invoice", new
        {
            documentType = 5, // ReceivedInvoice
            clientId = supplierId,
            issuerId = 0, // the server uses our own company
            currencyId,
            customDocumentNumber = documentNumber,
            invoiceItem = new[]
            {
                new { description = "E2E received item", quantity = 1, unit = "ks", unitPrice = 1000, vatRatePercentage = vatPercentage }
            }
        }));
        return created.GetProperty("id").GetInt64();
    }

    /// <summary>
    /// Makes sure the list of issued invoices holds at least one Draft AND one issued (Completed) invoice.
    /// Idempotent: it only creates what is missing.
    /// </summary>
    public static async Task EnsureIssuedInvoicesAsync(TestConfiguration config)
    {
        // Against a remote environment the tests use whatever data is there (as they always did).
        if (!new Uri(config.ApiUrl).IsLoopback) return;

        using var http = await OpenAsync(config);

        var page = await ReadAsync(await http.GetAsync("api/invoice/paged?pageSize=100"));
        var statuses = page.GetProperty("items").EnumerateArray().Select(i => i.GetProperty("status").GetInt32()).ToList();
        var needDraft = !statuses.Contains(1);   // EInvoiceStatus.Draft
        var needIssued = !statuses.Any(s => s is 2 or 3 or 6); // Completed, Paid or PartiallyPaid

        if (!needDraft && !needIssued) return;

        var clients = await ReadAsync(await http.GetAsync("api/client"));
        var customer = clients.EnumerateArray().FirstOrDefault(c => !c.GetProperty("isIssuer").GetBoolean());
        var customerId = customer.ValueKind == JsonValueKind.Undefined
            ? await CreateClientAsync(config, "E2E Seed Customer")
            : customer.GetProperty("id").GetInt64();
        // The client list holds customers only; our own company comes from its own endpoint.
        var issuerId = (await ReadAsync(await http.GetAsync("api/client/issuer"))).GetProperty("id").GetInt64();
        var currencies = await ReadAsync(await http.GetAsync("api/currency/active"));
        var currencyId = currencies.EnumerateArray().First().GetProperty("id").GetInt64();

        async Task<long> CreateDraftAsync()
        {
            var created = await ReadAsync(await http.PostAsJsonAsync("api/invoice", new
            {
                documentType = 1, // Invoice
                clientId = customerId,
                issuerId,
                currencyId,
                invoiceItem = new[] { new { description = "E2E seed item", quantity = 1, unit = "ks", unitPrice = 100, vatRatePercentage = 0 } }
            }));
            return created.GetProperty("id").GetInt64();
        }

        if (needDraft) await CreateDraftAsync();
        if (needIssued)
        {
            var id = await CreateDraftAsync();
            await ReadAsync(await http.PostAsync($"api/invoice/{id}/complete", null));
        }
    }
}

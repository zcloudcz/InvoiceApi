using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Fakvio.Contracts.Common.Pagination;
using Fakvio.Contracts.Dto.Client;
using Fakvio.Contracts.Dto.Dashboard;
using Fakvio.Contracts.Dto.Email;
using Fakvio.Contracts.Dto.Invoice;
using Fakvio.Contracts.Dto.InvoiceTemplate;
using Fakvio.Contracts.Dto.ReceivedInvoice;
using Fakvio.Contracts.Dto.Tax;
using Fakvio.Contracts.Dto.VatReport;

namespace Fakvio.McpServer.Client;

/// <summary>
/// Typed HttpClient wrapper that calls the Fakvio REST API.
/// Registered via AddHttpClient&lt;IFakvioApiClient, FakvioApiClient&gt;() in DI.
///
/// Junior note: A "typed HttpClient" means the DI container creates one HttpClient
/// instance per FakvioApiClient and configures it (base URL, auth header) automatically.
/// We just call _http.GetFromJsonAsync() etc. — no manual URL building needed.
/// </summary>
public class FakvioApiClient : IFakvioApiClient
{
    private readonly HttpClient _http;

    /// <summary>
    /// JSON serializer options matching the API's camelCase convention.
    /// PropertyNameCaseInsensitive allows deserializing PascalCase C# properties
    /// from camelCase JSON responses.
    /// </summary>
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true
    };

    public FakvioApiClient(HttpClient http)
    {
        _http = http;
    }

    // ── Invoice endpoints ──────────────────────────────────────────────

    public async Task<PagedResult<InvoiceDto>> GetInvoicesPagedAsync(InvoiceFilterDto filter, CancellationToken ct = default)
    {
        // Build query string from filter DTO properties
        var query = BuildInvoiceFilterQuery(filter);
        var result = await _http.GetFromJsonAsync<PagedResult<InvoiceDto>>($"api/invoice/paged{query}", JsonOptions, ct);
        return result ?? new PagedResult<InvoiceDto>();
    }

    public async Task<InvoiceDto?> GetInvoiceByIdAsync(long id, CancellationToken ct = default)
    {
        var response = await _http.GetAsync($"api/invoice/{id}", ct);

        // 404 = invoice not found → return null instead of throwing
        if (response.StatusCode == HttpStatusCode.NotFound)
            return null;

        await EnsureSuccessAsync(response, ct);
        return await response.Content.ReadFromJsonAsync<InvoiceDto>(JsonOptions, ct);
    }

    public async Task<InvoiceDto?> GetInvoiceByDocumentNumberAsync(string documentNumber, CancellationToken ct = default)
    {
        var response = await _http.GetAsync($"api/invoice/by-number/{Uri.EscapeDataString(documentNumber)}", ct);

        if (response.StatusCode == HttpStatusCode.NotFound)
            return null;

        await EnsureSuccessAsync(response, ct);
        return await response.Content.ReadFromJsonAsync<InvoiceDto>(JsonOptions, ct);
    }

    public async Task<InvoiceDto> CreateInvoiceAsync(CreateInvoiceDto dto, CancellationToken ct = default)
    {
        var response = await _http.PostAsJsonAsync("api/invoice", dto, JsonOptions, ct);
        await EnsureSuccessAsync(response, ct);
        return (await response.Content.ReadFromJsonAsync<InvoiceDto>(JsonOptions, ct))!;
    }

    public async Task<InvoiceDto> CompleteInvoiceAsync(long id, CancellationToken ct = default)
    {
        // POST with empty body — the API only needs the invoice ID in the URL
        var response = await _http.PostAsync($"api/invoice/{id}/complete", null, ct);
        await EnsureSuccessAsync(response, ct);
        return (await response.Content.ReadFromJsonAsync<InvoiceDto>(JsonOptions, ct))!;
    }

    public async Task<InvoiceDto> MarkInvoiceAsPaidAsync(long id, CancellationToken ct = default)
    {
        var response = await _http.PostAsync($"api/invoice/{id}/mark-paid", null, ct);
        await EnsureSuccessAsync(response, ct);
        return (await response.Content.ReadFromJsonAsync<InvoiceDto>(JsonOptions, ct))!;
    }

    public async Task DeleteInvoiceAsync(long id, CancellationToken ct = default)
    {
        var response = await _http.DeleteAsync($"api/invoice/{id}", ct);
        await EnsureSuccessAsync(response, ct);
    }

    public async Task SendInvoiceEmailAsync(long id, SendInvoiceEmailDto dto, CancellationToken ct = default)
    {
        var response = await _http.PostAsJsonAsync($"api/invoice/{id}/send-email", dto, JsonOptions, ct);
        await EnsureSuccessAsync(response, ct);
    }

    /// <summary>
    /// Downloads the invoice PDF as raw bytes from GET /api/invoice/{id}/pdf.
    /// </summary>
    public async Task<byte[]> ExportInvoicePdfAsync(long id, CancellationToken ct = default)
    {
        var response = await _http.GetAsync($"api/invoice/{id}/pdf", ct);
        await EnsureSuccessAsync(response, ct);
        return await response.Content.ReadAsByteArrayAsync(ct);
    }

    /// <summary>
    /// Downloads the invoice ISDOC XML as raw bytes from GET /api/invoice/{id}/isdoc.
    /// ISDOC is the Czech electronic invoice standard used by Pohoda, Money, Helios.
    /// </summary>
    public async Task<byte[]> ExportInvoiceIsdocAsync(long id, CancellationToken ct = default)
    {
        var response = await _http.GetAsync($"api/invoice/{id}/isdoc", ct);
        await EnsureSuccessAsync(response, ct);
        return await response.Content.ReadAsByteArrayAsync(ct);
    }

    // ── Client endpoints ───────────────────────────────────────────────

    public async Task<PagedResult<ClientDto>> GetClientsPagedAsync(ClientFilterDto filter, CancellationToken ct = default)
    {
        var query = BuildClientFilterQuery(filter);
        var result = await _http.GetFromJsonAsync<PagedResult<ClientDto>>($"api/client/paged{query}", JsonOptions, ct);
        return result ?? new PagedResult<ClientDto>();
    }

    public async Task<ClientDto?> GetClientByIdAsync(long id, CancellationToken ct = default)
    {
        var response = await _http.GetAsync($"api/client/{id}", ct);

        if (response.StatusCode == HttpStatusCode.NotFound)
            return null;

        await EnsureSuccessAsync(response, ct);
        return await response.Content.ReadFromJsonAsync<ClientDto>(JsonOptions, ct);
    }

    public async Task<ClientDto> CreateClientAsync(CreateClientDto dto, CancellationToken ct = default)
    {
        var response = await _http.PostAsJsonAsync("api/client", dto, JsonOptions, ct);
        await EnsureSuccessAsync(response, ct);
        return (await response.Content.ReadFromJsonAsync<ClientDto>(JsonOptions, ct))!;
    }

    public async Task<ClientDto> UpdateClientAsync(long id, UpdateClientDto dto, CancellationToken ct = default)
    {
        var response = await _http.PutAsJsonAsync($"api/client/{id}", dto, JsonOptions, ct);
        await EnsureSuccessAsync(response, ct);
        return (await response.Content.ReadFromJsonAsync<ClientDto>(JsonOptions, ct))!;
    }

    public async Task<ClientDto?> FetchFromAresAsync(string registrationNumber, CancellationToken ct = default)
    {
        var response = await _http.GetAsync($"api/client/ares/{Uri.EscapeDataString(registrationNumber)}", ct);

        if (response.StatusCode == HttpStatusCode.NotFound || response.StatusCode == HttpStatusCode.BadRequest)
            return null;

        await EnsureSuccessAsync(response, ct);
        return await response.Content.ReadFromJsonAsync<ClientDto>(JsonOptions, ct);
    }

    public async Task<ClientDto?> GetIssuerAsync(CancellationToken ct = default)
    {
        var response = await _http.GetAsync("api/client/issuer", ct);

        if (response.StatusCode == HttpStatusCode.NotFound)
            return null;

        await EnsureSuccessAsync(response, ct);
        return await response.Content.ReadFromJsonAsync<ClientDto>(JsonOptions, ct);
    }

    // ── Invoice Template endpoints ─────────────────────────────────────

    public async Task<List<InvoiceTemplateDto>> GetActiveTemplatesAsync(string? documentType = null, CancellationToken ct = default)
    {
        var query = documentType != null ? $"?documentType={Uri.EscapeDataString(documentType)}" : "";
        var result = await _http.GetFromJsonAsync<List<InvoiceTemplateDto>>($"api/invoicetemplate/active{query}", JsonOptions, ct);
        return result ?? [];
    }

    public async Task<InvoiceTemplateDto?> GetTemplateByIdAsync(long id, CancellationToken ct = default)
    {
        var response = await _http.GetAsync($"api/invoicetemplate/{id}", ct);

        if (response.StatusCode == HttpStatusCode.NotFound)
            return null;

        await EnsureSuccessAsync(response, ct);
        return await response.Content.ReadFromJsonAsync<InvoiceTemplateDto>(JsonOptions, ct);
    }

    public async Task<InvoiceDto> CreateInvoiceFromTemplateAsync(long templateId, CreateInvoiceFromTemplateDto dto, CancellationToken ct = default)
    {
        var response = await _http.PostAsJsonAsync($"api/invoicetemplate/{templateId}/create-invoice", dto, JsonOptions, ct);
        await EnsureSuccessAsync(response, ct);
        return (await response.Content.ReadFromJsonAsync<InvoiceDto>(JsonOptions, ct))!;
    }

    // ── Dashboard endpoints ────────────────────────────────────────────

    public async Task<DashboardDto> GetDashboardAsync(CancellationToken ct = default)
    {
        var result = await _http.GetFromJsonAsync<DashboardDto>("api/dashboard", JsonOptions, ct);
        return result ?? new DashboardDto();
    }

    // ── Private helpers ────────────────────────────────────────────────

    // ── Received Invoice endpoints ──────────────────────────────────────

    public async Task<PagedResult<ReceivedInvoiceDto>> GetReceivedInvoicesPagedAsync(
        ReceivedInvoiceFilterDto filter, CancellationToken ct = default)
    {
        var query = BuildReceivedInvoiceFilterQuery(filter);
        var response = await _http.GetAsync($"api/received-invoice/paged{query}", ct);
        await EnsureSuccessAsync(response, ct);
        return (await response.Content.ReadFromJsonAsync<PagedResult<ReceivedInvoiceDto>>(JsonOptions, ct))!;
    }

    public async Task<ReceivedInvoiceDto?> GetReceivedInvoiceByIdAsync(long id, CancellationToken ct = default)
    {
        var response = await _http.GetAsync($"api/received-invoice/{id}", ct);
        if (response.StatusCode == HttpStatusCode.NotFound) return null;
        await EnsureSuccessAsync(response, ct);
        return await response.Content.ReadFromJsonAsync<ReceivedInvoiceDto>(JsonOptions, ct);
    }

    public async Task<ReceivedInvoiceDto> CreateReceivedInvoiceAsync(
        CreateReceivedInvoiceDto dto, CancellationToken ct = default)
    {
        var response = await _http.PostAsJsonAsync("api/received-invoice", dto, JsonOptions, ct);
        await EnsureSuccessAsync(response, ct);
        return (await response.Content.ReadFromJsonAsync<ReceivedInvoiceDto>(JsonOptions, ct))!;
    }

    public async Task<ReceivedInvoiceDto> ApproveReceivedInvoiceAsync(long id, CancellationToken ct = default)
    {
        var response = await _http.PostAsync($"api/received-invoice/{id}/approve", null, ct);
        await EnsureSuccessAsync(response, ct);
        return (await response.Content.ReadFromJsonAsync<ReceivedInvoiceDto>(JsonOptions, ct))!;
    }

    public async Task<ReceivedInvoiceDto> MarkReceivedInvoicePaidAsync(long id, CancellationToken ct = default)
    {
        var response = await _http.PostAsync($"api/received-invoice/{id}/mark-paid", null, ct);
        await EnsureSuccessAsync(response, ct);
        return (await response.Content.ReadFromJsonAsync<ReceivedInvoiceDto>(JsonOptions, ct))!;
    }

    public async Task DeleteReceivedInvoiceAsync(long id, CancellationToken ct = default)
    {
        var response = await _http.DeleteAsync($"api/received-invoice/{id}", ct);
        await EnsureSuccessAsync(response, ct);
    }

    // ── VAT Report endpoints ─────────────────────────────────────────────

    public async Task<VatReportDto> GetVatReportAsync(DateTime from, DateTime to, CancellationToken ct = default)
    {
        var response = await _http.GetAsync($"api/vat-report?from={from:O}&to={to:O}", ct);
        await EnsureSuccessAsync(response, ct);
        return (await response.Content.ReadFromJsonAsync<VatReportDto>(JsonOptions, ct))!;
    }

    // ── Tax estimation endpoints ─────────────────────────────────────────

    public async Task<TaxEstimationResult> EstimateTaxAsync(
        TaxEstimationRequest request, CancellationToken ct = default)
    {
        var response = await _http.PostAsJsonAsync("api/tax/estimate", request, JsonOptions, ct);
        await EnsureSuccessAsync(response, ct);
        return (await response.Content.ReadFromJsonAsync<TaxEstimationResult>(JsonOptions, ct))!;
    }

    public async Task<List<TaxEstimationResult>> CompareTaxRegimesAsync(
        decimal grossIncome, string country, int year,
        bool isMainActivity, decimal? actualExpenses,
        CancellationToken ct = default)
    {
        var url = $"api/tax/compare?grossIncome={grossIncome}&country={country}&year={year}" +
                  $"&isMainActivity={isMainActivity}";
        if (actualExpenses.HasValue)
            url += $"&actualExpenses={actualExpenses.Value}";

        var result = await _http.GetFromJsonAsync<List<TaxEstimationResult>>(url, JsonOptions, ct);
        return result ?? [];
    }

    public async Task<AnnualIncomeDto> GetAnnualIncomeAsync(int year, CancellationToken ct = default)
    {
        var result = await _http.GetFromJsonAsync<AnnualIncomeDto>($"api/tax/income/{year}", JsonOptions, ct);
        return result ?? new AnnualIncomeDto { Year = year };
    }

    public async Task<InsuranceAdvanceDto?> GetInsuranceAdvanceAsync(CancellationToken ct = default)
    {
        var response = await _http.GetAsync("api/tax/insurance-advance", ct);
        if (response.StatusCode == HttpStatusCode.NoContent)
            return null;
        await EnsureSuccessAsync(response, ct);
        return await response.Content.ReadFromJsonAsync<InsuranceAdvanceDto>(JsonOptions, ct);
    }

    public async Task<TaxYearConfigDto?> GetTaxConfigAsync(string country, int year, CancellationToken ct = default)
    {
        var response = await _http.GetAsync($"api/tax/config/{country}/{year}", ct);
        if (response.StatusCode == HttpStatusCode.NotFound)
            return null;
        await EnsureSuccessAsync(response, ct);
        return await response.Content.ReadFromJsonAsync<TaxYearConfigDto>(JsonOptions, ct);
    }

    // ── Query string builders ────────────────────────────────────────────

    /// <summary>
    /// Builds a query string from ReceivedInvoiceFilterDto properties.
    /// </summary>
    private static string BuildReceivedInvoiceFilterQuery(ReceivedInvoiceFilterDto f)
    {
        var parts = new List<string>();

        if (f.Page > 1) parts.Add($"page={f.Page}");
        if (f.PageSize != 50) parts.Add($"pageSize={f.PageSize}");
        if (!string.IsNullOrEmpty(f.SortBy)) parts.Add($"sortBy={Uri.EscapeDataString(f.SortBy)}");
        if (!string.IsNullOrEmpty(f.SortDirection) && f.SortDirection != "asc")
            parts.Add($"sortDirection={Uri.EscapeDataString(f.SortDirection)}");

        if (!string.IsNullOrEmpty(f.Search)) parts.Add($"search={Uri.EscapeDataString(f.Search)}");
        if (f.Status.HasValue) parts.Add($"status={f.Status.Value}");
        if (f.SupplierId.HasValue) parts.Add($"supplierId={f.SupplierId.Value}");
        if (f.IssueDateFrom.HasValue) parts.Add($"issueDateFrom={f.IssueDateFrom.Value:O}");
        if (f.IssueDateTo.HasValue) parts.Add($"issueDateTo={f.IssueDateTo.Value:O}");
        if (f.DueDateFrom.HasValue) parts.Add($"dueDateFrom={f.DueDateFrom.Value:O}");
        if (f.DueDateTo.HasValue) parts.Add($"dueDateTo={f.DueDateTo.Value:O}");
        if (f.IsOverdue.HasValue) parts.Add($"isOverdue={f.IsOverdue.Value.ToString().ToLowerInvariant()}");
        if (!string.IsNullOrEmpty(f.Currency)) parts.Add($"currency={Uri.EscapeDataString(f.Currency)}");
        if (f.MinAmount.HasValue) parts.Add($"minAmount={f.MinAmount.Value}");
        if (f.MaxAmount.HasValue) parts.Add($"maxAmount={f.MaxAmount.Value}");

        return parts.Count > 0 ? "?" + string.Join("&", parts) : "";
    }

    /// <summary>
    /// Checks the HTTP response and throws a descriptive exception on failure.
    /// Tries to extract {"message":"..."} from the error body (same pattern as Blazor ApiClientBase).
    ///
    /// Junior note: This gives us clean error messages like "Invoice not found"
    /// instead of cryptic "500 Internal Server Error".
    /// </summary>
    private static async Task EnsureSuccessAsync(HttpResponseMessage response, CancellationToken ct)
    {
        if (response.IsSuccessStatusCode)
            return;

        var body = await response.Content.ReadAsStringAsync(ct);

        // Try to extract a user-friendly message from the API error response
        string errorMessage;
        try
        {
            using var doc = JsonDocument.Parse(body);
            errorMessage = doc.RootElement.TryGetProperty("message", out var msg)
                ? msg.GetString() ?? body
                : body;
        }
        catch
        {
            // Response body is not JSON — use raw text
            errorMessage = string.IsNullOrWhiteSpace(body)
                ? response.ReasonPhrase ?? "Unknown error"
                : body;
        }

        throw new HttpRequestException(
            $"API returned {(int)response.StatusCode} {response.StatusCode}: {errorMessage}",
            inner: null,
            response.StatusCode);
    }

    /// <summary>
    /// Builds a query string from InvoiceFilterDto properties.
    /// Only includes non-null/non-default values to keep URLs clean.
    /// </summary>
    private static string BuildInvoiceFilterQuery(InvoiceFilterDto f)
    {
        var parts = new List<string>();

        // Pagination (inherited from PaginationParams)
        if (f.Page > 1) parts.Add($"page={f.Page}");
        if (f.PageSize != 50) parts.Add($"pageSize={f.PageSize}");
        if (!string.IsNullOrEmpty(f.SortBy)) parts.Add($"sortBy={Uri.EscapeDataString(f.SortBy)}");
        if (!string.IsNullOrEmpty(f.SortDirection) && f.SortDirection != "asc")
            parts.Add($"sortDirection={Uri.EscapeDataString(f.SortDirection)}");

        // Invoice-specific filters
        if (!string.IsNullOrEmpty(f.Search)) parts.Add($"search={Uri.EscapeDataString(f.Search)}");
        if (f.DocumentType.HasValue) parts.Add($"documentType={f.DocumentType.Value}");
        if (f.Status.HasValue) parts.Add($"status={f.Status.Value}");
        if (f.ClientId.HasValue) parts.Add($"clientId={f.ClientId.Value}");
        if (f.IssuerId.HasValue) parts.Add($"issuerId={f.IssuerId.Value}");
        if (f.IssueDateFrom.HasValue) parts.Add($"issueDateFrom={f.IssueDateFrom.Value:O}");
        if (f.IssueDateTo.HasValue) parts.Add($"issueDateTo={f.IssueDateTo.Value:O}");
        if (f.DueDateFrom.HasValue) parts.Add($"dueDateFrom={f.DueDateFrom.Value:O}");
        if (f.DueDateTo.HasValue) parts.Add($"dueDateTo={f.DueDateTo.Value:O}");
        if (f.IsOverdue.HasValue) parts.Add($"isOverdue={f.IsOverdue.Value.ToString().ToLowerInvariant()}");
        if (!string.IsNullOrEmpty(f.Currency)) parts.Add($"currency={Uri.EscapeDataString(f.Currency)}");
        if (f.MinAmount.HasValue) parts.Add($"minAmount={f.MinAmount.Value}");
        if (f.MaxAmount.HasValue) parts.Add($"maxAmount={f.MaxAmount.Value}");

        return parts.Count > 0 ? "?" + string.Join("&", parts) : "";
    }

    /// <summary>
    /// Builds a query string from ClientFilterDto properties.
    /// </summary>
    private static string BuildClientFilterQuery(ClientFilterDto f)
    {
        var parts = new List<string>();

        // Pagination
        if (f.Page > 1) parts.Add($"page={f.Page}");
        if (f.PageSize != 50) parts.Add($"pageSize={f.PageSize}");
        if (!string.IsNullOrEmpty(f.SortBy)) parts.Add($"sortBy={Uri.EscapeDataString(f.SortBy)}");
        if (!string.IsNullOrEmpty(f.SortDirection) && f.SortDirection != "asc")
            parts.Add($"sortDirection={Uri.EscapeDataString(f.SortDirection)}");

        // Client-specific filters
        if (!string.IsNullOrEmpty(f.Search)) parts.Add($"search={Uri.EscapeDataString(f.Search)}");
        if (f.IsVatPayer.HasValue) parts.Add($"isVatPayer={f.IsVatPayer.Value.ToString().ToLowerInvariant()}");
        if (f.IsIssuer.HasValue) parts.Add($"isIssuer={f.IsIssuer.Value.ToString().ToLowerInvariant()}");
        if (f.IncludeInactive) parts.Add("includeInactive=true");
        if (!string.IsNullOrEmpty(f.City)) parts.Add($"city={Uri.EscapeDataString(f.City)}");
        if (!string.IsNullOrEmpty(f.Country)) parts.Add($"country={Uri.EscapeDataString(f.Country)}");

        return parts.Count > 0 ? "?" + string.Join("&", parts) : "";
    }
}

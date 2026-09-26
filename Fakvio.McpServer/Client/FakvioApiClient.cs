using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Fakvio.Contracts.Common.Pagination;
using Fakvio.Contracts.Dto.ApiKey;
using Fakvio.Contracts.Dto.Client;
using Fakvio.Contracts.Dto.Dashboard;
using Fakvio.Contracts.Dto.Email;
using Fakvio.Contracts.Dto.Invoice;
using Fakvio.Contracts.Dto.InvoiceTemplate;
using Fakvio.Contracts.Dto.Readiness;
using Fakvio.Contracts.Dto.FileAttachment;
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

    // ── File attachment endpoints ────────────────────────────────────

    public async Task<FileAttachmentDto> UploadFileAttachmentAsync(
        string entityName, long recordId, string fileName, string contentType, byte[] content,
        string? description = null, CancellationToken ct = default)
    {
        // The API endpoint expects multipart/form-data (IFormFile + form fields), not JSON.
        // Field names must match FileAttachmentController.Upload parameters exactly.
        using var form = new MultipartFormDataContent();
        var file = new ByteArrayContent(content);
        file.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue(contentType);
        form.Add(file, "file", fileName);
        form.Add(new StringContent(entityName), "entityName");
        form.Add(new StringContent(recordId.ToString()), "recordId");
        if (!string.IsNullOrEmpty(description))
            form.Add(new StringContent(description), "description");

        var response = await _http.PostAsync("api/file-attachment/upload", form, ct);
        await EnsureSuccessAsync(response, ct);
        return (await response.Content.ReadFromJsonAsync<FileAttachmentDto>(JsonOptions, ct))!;
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

    // ── Readiness endpoints ────────────────────────────────────────────

    public async Task<ReadinessReportDto?> GetReadinessAsync(long? issuerId = null, CancellationToken ct = default)
    {
        // No issuerId → the whole tenant is checked, which is what the API does without the filter.
        var query = issuerId.HasValue ? $"?issuerId={issuerId.Value}" : string.Empty;
        var response = await _http.GetAsync($"api/readiness{query}", ct);

        // A 404 is a domain answer *only* when we asked for one specific issuer — the endpoint
        // returns it exactly for "that issuerId is not in this tenant" (ReadinessController).
        // Without a filter it never 404s, so a 404 there means the route itself is unreachable
        // (wrong base URL, endpoint not deployed, proxy). Let that fall through to
        // EnsureSuccessAsync so it surfaces as a transport error instead of being mistaken
        // for a missing issuer. An unfinished setup is a normal 200 with issues either way.
        if (issuerId.HasValue && response.StatusCode == HttpStatusCode.NotFound)
            return null;

        await EnsureSuccessAsync(response, ct);
        return await response.Content.ReadFromJsonAsync<ReadinessReportDto>(JsonOptions, ct);
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

        // A TENANT_NOT_READY 400 carries a structured payload (code, missingFields, issues —
        // DEVGUIDE §4.12) that a flattened "message" string would throw away (#342). Caught
        // here, before the generic message-only path below, so every caller of this client
        // gets the same structured exception the HTTP body actually contains.
        if (response.StatusCode == HttpStatusCode.BadRequest &&
            TryParseTenantNotReady(body) is { } tenantNotReady)
            throw tenantNotReady;

        // Try to extract a user-friendly message from the API error response.
        // errorMessage (unsanitized, may repeat the raw body) goes into the exception's
        // Message — server log only. safeMessage (#279 / N2.2) is the ONLY part that may
        // ever reach the AI client, via FakvioApiException.SafeMessage, and only exists
        // when the body is actually JSON with a string message (object shape) or a bare
        // JSON string (e.g. TaxController's BadRequest("...") calls).
        string errorMessage;
        string? safeMessage;
        try
        {
            using var doc = JsonDocument.Parse(body);
            var root = doc.RootElement;

            if (root.ValueKind == JsonValueKind.Object &&
                root.TryGetProperty("message", out var msg) &&
                msg.ValueKind == JsonValueKind.String)
            {
                safeMessage = msg.GetString();
                errorMessage = safeMessage ?? body;
            }
            else if (root.ValueKind == JsonValueKind.String)
            {
                safeMessage = root.GetString();
                errorMessage = safeMessage ?? body;
            }
            else
            {
                safeMessage = null;
                errorMessage = body;
            }
        }
        catch (JsonException)
        {
            // Response body is not JSON — use raw text for the log, nothing safe to relay.
            safeMessage = null;
            errorMessage = string.IsNullOrWhiteSpace(body)
                ? response.ReasonPhrase ?? "Unknown error"
                : body;
        }

        throw new FakvioApiException(
            $"API returned {(int)response.StatusCode} {response.StatusCode}: {errorMessage}",
            response.StatusCode,
            safeMessage);
    }

    /// <summary>
    /// Parses the <c>{ code, message, missingFields, issues }</c> shape (DEVGUIDE §4.12) out of a
    /// 400 body. Returns null for anything else — a malformed body or an unrelated 400 — so the
    /// caller falls back to the generic message-only exception instead of throwing a parse error.
    ///
    /// Junior note — why every read below is preceded by a <see cref="JsonValueKind"/> check
    /// instead of just "is the property there": <see cref="JsonElement.TryGetProperty(string, out JsonElement)"/>
    /// does not return false on a non-object root, it throws <see cref="InvalidOperationException"/>,
    /// and <see cref="JsonElement.GetString"/> throws the same on an element that is not a string.
    /// Neither is a <see cref="JsonException"/>, so without these guards such an exception would
    /// escape <see cref="EnsureSuccessAsync"/> entirely. That is not hypothetical: a 400 body is
    /// often a bare JSON string (<c>TaxController</c> answers <c>BadRequest("Gross income cannot be
    /// negative.")</c>, reached from <see cref="EstimateTaxAsync"/>), which would turn an ordinary
    /// validation message into an internal type error for every MCP tool on this client.
    /// </summary>
    private static TenantNotReadyApiException? TryParseTenantNotReady(string body)
    {
        try
        {
            using var doc = JsonDocument.Parse(body);
            var root = doc.RootElement;

            if (root.ValueKind != JsonValueKind.Object)
                return null;

            if (!root.TryGetProperty("code", out var codeEl) ||
                codeEl.ValueKind != JsonValueKind.String ||
                codeEl.GetString() != TenantNotReadyApiException.ErrorCode)
                return null;

            var message = root.TryGetProperty("message", out var msgEl) && msgEl.ValueKind == JsonValueKind.String
                ? msgEl.GetString()!
                : body;
            var missingFields = root.TryGetProperty("missingFields", out var mfEl) && mfEl.ValueKind == JsonValueKind.Array
                ? JsonSerializer.Deserialize<List<string>>(mfEl.GetRawText(), JsonOptions) ?? []
                : [];
            var issues = root.TryGetProperty("issues", out var issuesEl) && issuesEl.ValueKind == JsonValueKind.Array
                ? JsonSerializer.Deserialize<List<ReadinessIssueDto>>(issuesEl.GetRawText(), JsonOptions) ?? []
                : [];

            return new TenantNotReadyApiException(message, missingFields, issues);
        }
        catch (JsonException)
        {
            // Body is not JSON at all, or an array element does not fit the DTO. Either way it
            // is not the TENANT_NOT_READY shape — fall through to the generic exception.
            return null;
        }
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

    // ── Identity ──────────────────────────────────────────────────────────

    public async Task<ApiKeyIdentityDto?> GetIdentityAsync(CancellationToken ct = default)
    {
        var response = await _http.GetAsync("api/api-key/me", ct);

        // 401/403 = the API looked at the credential and said no. That is a domain answer
        // ("this key does not work"), not a failure, so it comes back as null and the caller
        // decides what to do — the HTTP gate turns it into a 401 for the MCP client.
        if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
            return null;

        // Anything else non-2xx (API down, wrong base URL, 5xx) throws on purpose: answering
        // "your key is invalid" to an unreachable API would send the operator hunting in the
        // wrong place.
        await EnsureSuccessAsync(response, ct);

        return await response.Content.ReadFromJsonAsync<ApiKeyIdentityDto>(JsonOptions, ct);
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

using Fakvio.Contracts.Dto.CompanyMembership;
using Fakvio.Contracts.Dto.Feedback;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Fakvio.Contracts.Common.Pagination;
using Fakvio.Contracts.Dto.ApiKey;
using Fakvio.Contracts.Dto.Client;
using Fakvio.Contracts.Dto.Currency;
using Fakvio.Contracts.Dto.Dashboard;
using Fakvio.Contracts.Dto.Email;
using Fakvio.Contracts.Dto.Invoice;
using Fakvio.Contracts.Dto.InvoiceTemplate;
using Fakvio.Contracts.Dto.NumberSequence;
using Fakvio.Contracts.Dto.PaymentMatching;
using Fakvio.Contracts.Dto.Readiness;
using Fakvio.Contracts.Dto.RecurringInvoice;
using Fakvio.Contracts.Dto.Reminder;
using Fakvio.Contracts.Dto.FileAttachment;
using Fakvio.Contracts.Dto.ReceivedInvoice;
using Fakvio.Contracts.Dto.Tax;
using Fakvio.Contracts.Dto.VatRate;
using Fakvio.Contracts.Dto.VatReport;
using Fakvio.Domain.Enums;

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

    public async Task<List<CompanyMembershipDto>> GetMyCompaniesAsync(CancellationToken ct = default)
    {
        using var response = await _http.GetAsync("api/my-companies", ct);
        await EnsureSuccessAsync(response, ct);
        return await response.Content.ReadFromJsonAsync<List<CompanyMembershipDto>>(JsonOptions, ct) ?? [];
    }

    public async Task<List<ManagedCompanyMembershipDto>> GetUserCompanyMembershipsAsync(long userId, CancellationToken ct = default)
    {
        using var response = await _http.GetAsync($"api/user/{userId}/memberships", ct);
        await EnsureSuccessAsync(response, ct);
        return await response.Content.ReadFromJsonAsync<List<ManagedCompanyMembershipDto>>(JsonOptions, ct) ?? [];
    }

    public async Task<ManagedCompanyMembershipDto> UpdateUserCompanyMembershipAsync(long userId, long companyId, UpdateCompanyMembershipDto input, CancellationToken ct = default)
    {
        using var response = await _http.PutAsJsonAsync($"api/user/{userId}/memberships/{companyId}", input, JsonOptions, ct);
        await EnsureSuccessAsync(response, ct);
        return (await response.Content.ReadFromJsonAsync<ManagedCompanyMembershipDto>(JsonOptions, ct))!;
    }

    public async Task<CompanyMembershipDto> CreateMyCompanyAsync(CreateMyCompanyDto company, CancellationToken ct = default)
    {
        using var response = await _http.PostAsJsonAsync("api/my-companies", company, JsonOptions, ct);
        await EnsureSuccessAsync(response, ct);
        return (await response.Content.ReadFromJsonAsync<CompanyMembershipDto>(JsonOptions, ct))!;
    }

    public async Task<CompanyMembershipDto> RetryCompanyProvisioningAsync(long companyId, CancellationToken ct = default)
    {
        using var response = await _http.PostAsync($"api/my-companies/{companyId}/retry-provisioning", null, ct);
        await EnsureSuccessAsync(response, ct);
        return (await response.Content.ReadFromJsonAsync<CompanyMembershipDto>(JsonOptions, ct))!;
    }

    // All feedback operations use the same configured HttpClient and therefore the same
    // credentials as the other tools. Never attach a user/company override here.
    public async Task<FeedbackDto> CreateFeedbackAsync(CreateFeedbackDto dto, CancellationToken ct = default)
    {
        using var response = await _http.PostAsJsonAsync("api/feedback", dto, JsonOptions, ct);
        await EnsureSuccessAsync(response, ct);
        return (await response.Content.ReadFromJsonAsync<FeedbackDto>(JsonOptions, ct))!;
    }

    public Task<PagedResult<FeedbackDto>> GetFeedbackAsync(FeedbackFilterDto filter, CancellationToken ct = default)
        => GetFeedbackPageAsync("api/feedback", filter, ct);

    public Task<PagedResult<FeedbackDto>> GetAdminFeedbackAsync(FeedbackFilterDto filter, CancellationToken ct = default)
        => GetFeedbackPageAsync("api/sysadmin/feedback", filter, ct);

    public Task<FeedbackDto> GetFeedbackByIdAsync(long id, CancellationToken ct = default)
        => GetFeedbackDetailAsync($"api/feedback/{id}", ct);

    public Task<FeedbackDto> GetAdminFeedbackByIdAsync(long id, CancellationToken ct = default)
        => GetFeedbackDetailAsync($"api/sysadmin/feedback/{id}", ct);

    public async Task<FeedbackDto> UpdateFeedbackStatusAsync(long id, UpdateFeedbackStatusDto dto, CancellationToken ct = default)
    {
        using var response = await _http.PatchAsJsonAsync($"api/sysadmin/feedback/{id}", dto, JsonOptions, ct);
        await EnsureSuccessAsync(response, ct);
        return (await response.Content.ReadFromJsonAsync<FeedbackDto>(JsonOptions, ct))!;
    }

    private async Task<PagedResult<FeedbackDto>> GetFeedbackPageAsync(string path, FeedbackFilterDto filter, CancellationToken ct)
    {
        // Forward even invalid paging values: the API owns validation consistently for UI and MCP.
        var query = $"?page={filter.Page}&pageSize={filter.PageSize}";
        if (filter.Type.HasValue) query += $"&type={(int)filter.Type.Value}";
        if (filter.Status.HasValue) query += $"&status={(int)filter.Status.Value}";
        using var response = await _http.GetAsync(path + query, ct);
        await EnsureSuccessAsync(response, ct);
        return (await response.Content.ReadFromJsonAsync<PagedResult<FeedbackDto>>(JsonOptions, ct))!;
    }

    private async Task<FeedbackDto> GetFeedbackDetailAsync(string path, CancellationToken ct)
    {
        using var response = await _http.GetAsync(path, ct);
        // Inaccessible and missing reports both retain the API's 404; no ownership information leaks.
        await EnsureSuccessAsync(response, ct);
        return (await response.Content.ReadFromJsonAsync<FeedbackDto>(JsonOptions, ct))!;
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

    public async Task<InvoiceDto?> UpdateInvoiceAsync(long id, UpdateInvoiceDto dto, CancellationToken ct = default)
    {
        var response = await _http.PutAsJsonAsync($"api/invoice/{id}", dto, JsonOptions, ct);

        if (response.StatusCode == HttpStatusCode.NotFound)
            return null;

        await EnsureSuccessAsync(response, ct);
        return await response.Content.ReadFromJsonAsync<InvoiceDto>(JsonOptions, ct);
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

    /// <summary>
    /// Downloads the invoice UBL 2.1 / Peppol BIS Billing 3.0 XML as raw bytes from
    /// GET /api/invoice/{id}/ubl (ADR 0002, F1.7).
    /// </summary>
    public async Task<byte[]> ExportInvoiceUblAsync(long id, CancellationToken ct = default)
    {
        var response = await _http.GetAsync($"api/invoice/{id}/ubl", ct);
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

    public async Task<ClientDto?> AddBankAccountAsync(long clientId, CreateBankAccountDto dto, CancellationToken ct = default)
    {
        var response = await _http.PostAsJsonAsync($"api/client/{clientId}/bank-account", dto, JsonOptions, ct);

        if (response.StatusCode == HttpStatusCode.NotFound)
            return null;

        await EnsureSuccessAsync(response, ct);
        return await response.Content.ReadFromJsonAsync<ClientDto>(JsonOptions, ct);
    }

    // ── Invoice Template endpoints ─────────────────────────────────────

    public async Task<List<CurrencyDto>> GetActiveCurrenciesAsync(CancellationToken ct = default)
    {
        var result = await GetJsonAsync<List<CurrencyDto>>(_http, "api/currency/active", ct);
        return result ?? [];
    }

    public async Task<List<VatRateDto>> GetActiveVatRatesAsync(DateTime? date = null, CancellationToken ct = default)
    {
        var query = date.HasValue ? $"?date={date.Value:O}" : "";
        var result = await GetJsonAsync<List<VatRateDto>>(_http, $"api/vatrate/active{query}", ct);
        return result ?? [];
    }

    public async Task<List<NumberSequenceDto>> GetNumberSequencesAsync(
        EDocumentType? documentType = null, bool includeInactive = false, CancellationToken ct = default)
    {
        var parts = new List<string>();
        if (documentType.HasValue) parts.Add($"documentType={documentType.Value}");
        if (includeInactive) parts.Add("includeInactive=true");
        var query = parts.Count > 0 ? "?" + string.Join("&", parts) : "";

        var result = await GetJsonAsync<List<NumberSequenceDto>>(_http, $"api/numbersequence{query}", ct);
        return result ?? [];
    }

    public async Task<NumberSequenceDto?> GetNumberSequenceByIdAsync(long id, CancellationToken ct = default)
    {
        var response = await _http.GetAsync($"api/numbersequence/{id}", ct);

        if (response.StatusCode == HttpStatusCode.NotFound)
            return null;

        await EnsureSuccessAsync(response, ct);
        return await response.Content.ReadFromJsonAsync<NumberSequenceDto>(JsonOptions, ct);
    }

    public async Task<List<NumberSequenceFormatDto>> GetNumberSequenceFormatsAsync(
        bool includeInactive = false, CancellationToken ct = default)
    {
        var query = includeInactive ? "?includeInactive=true" : "";
        var result = await GetJsonAsync<List<NumberSequenceFormatDto>>(_http, $"api/numbersequence/formats{query}", ct);
        return result ?? [];
    }

    public async Task<NumberSequenceDto> CreateNumberSequenceAsync(CreateNumberSequenceDto dto, CancellationToken ct = default)
    {
        var response = await _http.PostAsJsonAsync("api/numbersequence", dto, JsonOptions, ct);
        await EnsureSuccessAsync(response, ct);
        return (await response.Content.ReadFromJsonAsync<NumberSequenceDto>(JsonOptions, ct))!;
    }

    public async Task<NumberSequenceDto?> UpdateNumberSequenceAsync(
        long id, UpdateNumberSequenceDto dto, CancellationToken ct = default)
    {
        var response = await _http.PutAsJsonAsync($"api/numbersequence/{id}", dto, JsonOptions, ct);

        if (response.StatusCode == HttpStatusCode.NotFound)
            return null;

        await EnsureSuccessAsync(response, ct);
        return await response.Content.ReadFromJsonAsync<NumberSequenceDto>(JsonOptions, ct);
    }

    public async Task<NumberSequenceDto?> SetDefaultNumberSequenceAsync(long id, CancellationToken ct = default)
    {
        var response = await _http.PostAsync($"api/numbersequence/{id}/set-default", content: null, ct);

        if (response.StatusCode == HttpStatusCode.NotFound)
            return null;

        await EnsureSuccessAsync(response, ct);
        return await response.Content.ReadFromJsonAsync<NumberSequenceDto>(JsonOptions, ct);
    }

    public async Task<PagedResult<BankTransactionDto>> GetPaymentsPagedAsync(
        EMatchStatus? status, EPaymentDirection? direction, DateTime? from, DateTime? to,
        int page, int pageSize, CancellationToken ct = default)
    {
        var parts = new List<string> { $"page={page}", $"pageSize={pageSize}" };
        if (status.HasValue) parts.Add($"status={status.Value}");
        if (direction.HasValue) parts.Add($"direction={direction.Value}");
        if (from.HasValue) parts.Add($"from={from.Value:O}");
        if (to.HasValue) parts.Add($"to={to.Value:O}");

        var query = "?" + string.Join("&", parts);
        var result = await GetJsonAsync<PagedResult<BankTransactionDto>>(_http, $"api/payment-matching/transactions{query}", ct);
        return result ?? new PagedResult<BankTransactionDto>([], 0, page, pageSize);
    }

    public async Task<BankTransactionDto?> GetPaymentByIdAsync(long id, CancellationToken ct = default)
    {
        var response = await _http.GetAsync($"api/payment-matching/transactions/{id}", ct);

        if (response.StatusCode == HttpStatusCode.NotFound)
            return null;

        await EnsureSuccessAsync(response, ct);
        return await response.Content.ReadFromJsonAsync<BankTransactionDto>(JsonOptions, ct);
    }

    public async Task<PagedResult<ReminderDto>> GetRemindersPagedAsync(ReminderFilterDto filter, CancellationToken ct = default)
    {
        var parts = new List<string> { $"page={filter.Page}", $"pageSize={filter.PageSize}" };
        if (filter.Status.HasValue) parts.Add($"status={filter.Status.Value}");
        if (filter.ClientId.HasValue) parts.Add($"clientId={filter.ClientId.Value}");
        if (filter.InvoiceId.HasValue) parts.Add($"invoiceId={filter.InvoiceId.Value}");
        if (filter.Level.HasValue) parts.Add($"level={filter.Level.Value}");
        if (!string.IsNullOrEmpty(filter.Search)) parts.Add($"search={Uri.EscapeDataString(filter.Search)}");
        if (filter.DateFrom.HasValue) parts.Add($"dateFrom={filter.DateFrom.Value:O}");
        if (filter.DateTo.HasValue) parts.Add($"dateTo={filter.DateTo.Value:O}");

        var query = "?" + string.Join("&", parts);
        var result = await GetJsonAsync<PagedResult<ReminderDto>>(_http, $"api/reminder/paged{query}", ct);
        return result ?? new PagedResult<ReminderDto>([], 0, filter.Page, filter.PageSize);
    }

    public async Task<List<ReminderDto>> GetRemindersByInvoiceAsync(long invoiceId, CancellationToken ct = default)
    {
        var result = await GetJsonAsync<List<ReminderDto>>(_http, $"api/reminder/invoice/{invoiceId}", ct);
        return result ?? [];
    }

    public async Task<ReminderSettingsDto> GetReminderSettingsAsync(CancellationToken ct = default)
    {
        var response = await _http.GetAsync("api/reminder/settings", ct);
        await EnsureSuccessAsync(response, ct);
        return (await response.Content.ReadFromJsonAsync<ReminderSettingsDto>(JsonOptions, ct))!;
    }

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

    // ── Recurring invoice schedule endpoints (DEVGUIDE §4.13) ────────────

    public async Task<List<RecurringInvoiceScheduleDto>> GetRecurringSchedulesAsync(
        long? templateId = null, CancellationToken ct = default)
    {
        var query = templateId.HasValue ? $"?templateId={templateId.Value}" : string.Empty;
        var result = await GetJsonAsync<List<RecurringInvoiceScheduleDto>>(_http, $"api/recurringinvoice{query}", ct);
        return result ?? [];
    }

    public async Task<RecurringInvoiceScheduleDto?> GetRecurringScheduleByIdAsync(long id, CancellationToken ct = default)
    {
        var response = await _http.GetAsync($"api/recurringinvoice/{id}", ct);
        if (response.StatusCode == HttpStatusCode.NotFound) return null;
        await EnsureSuccessAsync(response, ct);
        return await response.Content.ReadFromJsonAsync<RecurringInvoiceScheduleDto>(JsonOptions, ct);
    }

    public async Task<RecurringInvoiceScheduleDto> CreateRecurringScheduleAsync(
        CreateRecurringInvoiceScheduleDto dto, CancellationToken ct = default)
    {
        var response = await _http.PostAsJsonAsync("api/recurringinvoice", dto, JsonOptions, ct);
        await EnsureSuccessAsync(response, ct);
        return (await response.Content.ReadFromJsonAsync<RecurringInvoiceScheduleDto>(JsonOptions, ct))!;
    }

    public async Task<RecurringInvoiceScheduleDto> UpdateRecurringScheduleAsync(
        long id, UpdateRecurringInvoiceScheduleDto dto, CancellationToken ct = default)
    {
        var response = await _http.PutAsJsonAsync($"api/recurringinvoice/{id}", dto, JsonOptions, ct);
        await EnsureSuccessAsync(response, ct);
        return (await response.Content.ReadFromJsonAsync<RecurringInvoiceScheduleDto>(JsonOptions, ct))!;
    }

    public async Task<RecurringInvoiceScheduleDto> PauseRecurringScheduleAsync(long id, CancellationToken ct = default)
    {
        var response = await _http.PostAsync($"api/recurringinvoice/{id}/pause", null, ct);
        await EnsureSuccessAsync(response, ct);
        return (await response.Content.ReadFromJsonAsync<RecurringInvoiceScheduleDto>(JsonOptions, ct))!;
    }

    public async Task<RecurringInvoiceScheduleDto> ResumeRecurringScheduleAsync(long id, CancellationToken ct = default)
    {
        var response = await _http.PostAsync($"api/recurringinvoice/{id}/resume", null, ct);
        await EnsureSuccessAsync(response, ct);
        return (await response.Content.ReadFromJsonAsync<RecurringInvoiceScheduleDto>(JsonOptions, ct))!;
    }

    public async Task DeleteRecurringScheduleAsync(long id, CancellationToken ct = default)
    {
        var response = await _http.DeleteAsync($"api/recurringinvoice/{id}", ct);
        await EnsureSuccessAsync(response, ct);
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
    /// GET a URL and deserialize a success response, routing anything else through
    /// <see cref="EnsureSuccessAsync"/> so a 4xx/5xx becomes a <see cref="FakvioApiException"/>
    /// with a sanitized <c>SafeMessage</c> instead of a bare <see cref="HttpRequestException"/>.
    ///
    /// Junior note (Codex review, N2/N3 follow-up): <c>HttpClientJsonExtensions.GetFromJsonAsync</c>
    /// calls its own internal <c>EnsureSuccessStatusCode</c> on failure — that throws a plain
    /// <see cref="HttpRequestException"/> that never goes through our sanitization, so
    /// <see cref="Tools.McpToolError.ToJson"/> could not tell "read-only key on a write" from
    /// "server crashed" for these endpoints; both landed on <c>internal_error</c>. Every list/read
    /// endpoint added by story N2/N3 goes through this helper instead of calling
    /// <c>GetFromJsonAsync</c> directly, so it gets the same forbidden/not_found/validation_error
    /// mapping every other tool call already has.
    /// </summary>
    private static async Task<T?> GetJsonAsync<T>(HttpClient http, string url, CancellationToken ct)
    {
        var response = await http.GetAsync(url, ct);
        await EnsureSuccessAsync(response, ct);
        return await response.Content.ReadFromJsonAsync<T>(JsonOptions, ct);
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

using System.Net;
using System.Text;
using System.Text.Json;
using Fakvio.Contracts.Dto.AccountingExport;
using Fakvio.Contracts.Dto.Invoice;
using Fakvio.Contracts.Dto.PaymentMatching;
using Fakvio.Contracts.Dto.Readiness;
using Fakvio.UI.Shared.Models;
using Fakvio.Domain.Enums;
using Microsoft.AspNetCore.Components.Authorization;

// BulkOperationRequest, BulkOperationResult, BulkOperationError are in the Invoice DTO namespace

namespace Fakvio.UI.Shared.Services;

/// <summary>
/// Blazor service for communicating with the Fakvio endpoints.
/// Inherits ApiClientBase for shared auth, logging, impersonation, and error handling.
/// All HTTP calls go through the base class methods — no manual header management needed.
/// </summary>
/// <summary>Accounting export download: the XML bytes plus how many documents were left out.</summary>
public record AccountingExportFile(byte[] Content, int SkippedCount);

public class FakvioService : ApiClientBase
{
    public FakvioService(
        IHttpClientFactory httpClientFactory,
        ILogger<FakvioService> logger,
        AuthenticationStateProvider authStateProvider)
        : base(httpClientFactory, logger, authStateProvider)
    {
    }

    /// <summary>
    /// EU OSS destination country (ISO2) an invoice from this issuer to this client is ELIGIBLE for, or null
    /// (drives the opt-in checkbox; the server re-checks on save — DEVGUIDE §4.16).
    /// </summary>
    public async Task<string?> GetOssCountryAsync(long clientId, long issuerId, EDocumentType documentType)
    {
        try
        {
            var dto = await GetAsync<Fakvio.Contracts.Dto.OssReport.OssCountryDto>(
                $"/api/invoice/oss-country?clientId={clientId}&issuerId={issuerId}&documentType={(int)documentType}");
            return dto?.CountryCode;
        }
        catch (ApiException)
        {
            return null;
        }
    }

    /// <summary>
    /// Gets all invoices (non-paginated).
    /// </summary>
    public async Task<List<InvoiceDto>> GetAllAsync()
    {
        try
        {
            return await GetAsync<List<InvoiceDto>>("/api/invoice") ?? [];
        }
        catch (ApiException)
        {
            // Legacy consumers retain the fallback; interactive grids opt into visible errors.
            // Graceful degradation for list endpoints — show empty grid instead of crashing.
            // 401 is already handled by UnauthorizedRedirectHandler (redirects to /login).
            return [];
        }
    }

    /// <summary>
    /// Gets invoices with server-side pagination, filtering, and sorting.
    /// Builds a query string from all the optional filter parameters.
    /// </summary>
    public async Task<PagedResult<InvoiceDto>> GetPagedAsync(
        int page = 1,
        int pageSize = 50,
        string? search = null,
        string? sortBy = null,
        string? sortDirection = "asc",
        string? documentNumber = null,
        string? clientName = null,
        EDocumentType? documentType = null,
        EInvoiceStatus? status = null,
        long? clientId = null,
        long? issuerId = null,
        DateTime? issueDateFrom = null,
        DateTime? issueDateTo = null,
        DateTime? dueDateFrom = null,
        DateTime? dueDateTo = null,
        DateTime? taxableSupplyDateFrom = null,
        DateTime? taxableSupplyDateTo = null,
        bool? isOverdue = null,
        string? currency = null,
        decimal? minAmount = null,
        decimal? maxAmount = null,
        bool throwOnError = false)
    {
        try
        {
            // Build query string from all provided filter parameters
            var queryParams = new StringBuilder($"?Page={page}&PageSize={pageSize}");

            if (!string.IsNullOrWhiteSpace(search))
                queryParams.Append($"&Search={Uri.EscapeDataString(search)}");

            if (!string.IsNullOrWhiteSpace(sortBy))
                queryParams.Append($"&SortBy={sortBy}");

            if (!string.IsNullOrWhiteSpace(sortDirection))
                queryParams.Append($"&SortDirection={sortDirection}");

            if (!string.IsNullOrWhiteSpace(documentNumber))
                queryParams.Append($"&DocumentNumber={Uri.EscapeDataString(documentNumber)}");

            if (!string.IsNullOrWhiteSpace(clientName))
                queryParams.Append($"&ClientName={Uri.EscapeDataString(clientName)}");

            if (documentType.HasValue)
                queryParams.Append($"&DocumentType={documentType.Value}");

            if (status.HasValue)
                queryParams.Append($"&Status={status.Value}");

            if (clientId.HasValue)
                queryParams.Append($"&ClientId={clientId.Value}");

            if (issuerId.HasValue)
                queryParams.Append($"&IssuerId={issuerId.Value}");

            if (issueDateFrom.HasValue)
                queryParams.Append($"&IssueDateFrom={issueDateFrom.Value:yyyy-MM-dd}");

            if (issueDateTo.HasValue)
                queryParams.Append($"&IssueDateTo={issueDateTo.Value:yyyy-MM-dd}");

            if (dueDateFrom.HasValue)
                queryParams.Append($"&DueDateFrom={dueDateFrom.Value:yyyy-MM-dd}");

            if (dueDateTo.HasValue)
                queryParams.Append($"&DueDateTo={dueDateTo.Value:yyyy-MM-dd}");

            if (taxableSupplyDateFrom.HasValue)
                queryParams.Append($"&TaxableSupplyDateFrom={taxableSupplyDateFrom.Value:yyyy-MM-dd}");

            if (taxableSupplyDateTo.HasValue)
                queryParams.Append($"&TaxableSupplyDateTo={taxableSupplyDateTo.Value:yyyy-MM-dd}");

            if (isOverdue.HasValue)
                queryParams.Append($"&IsOverdue={isOverdue.Value}");

            if (!string.IsNullOrWhiteSpace(currency))
                queryParams.Append($"&Currency={Uri.EscapeDataString(currency)}");

            if (minAmount.HasValue)
                queryParams.Append($"&MinAmount={minAmount.Value}");

            if (maxAmount.HasValue)
                queryParams.Append($"&MaxAmount={maxAmount.Value}");

            return await GetAsync<PagedResult<InvoiceDto>>($"/api/invoice/paged{queryParams}")
                   ?? new PagedResult<InvoiceDto>();
        }
        catch (ApiException) when (!throwOnError)
        {
            // Graceful degradation for list endpoints — show empty grid instead of crashing.
            // 401 is already handled by UnauthorizedRedirectHandler (redirects to /login).
            return new PagedResult<InvoiceDto>();
        }
    }

    /// <summary>
    /// Gets a single invoice by ID, including line items.
    /// </summary>
    public async Task<InvoiceDto?> GetByIdAsync(long id)
    {
        return await GetAsync<InvoiceDto>($"/api/invoice/{id}");
    }

    /// <summary>
    /// Creates a new invoice (starts as Draft).
    /// </summary>
    public async Task<InvoiceDto?> CreateAsync(CreateInvoiceDto createDto)
    {
        return await PostAsync<CreateInvoiceDto, InvoiceDto>("/api/invoice", createDto);
    }

    /// <summary>
    /// Updates an existing draft invoice.
    /// </summary>
    public async Task<InvoiceDto?> UpdateAsync(long id, UpdateInvoiceDto updateDto)
    {
        return await PutAsync<UpdateInvoiceDto, InvoiceDto>($"/api/invoice/{id}", updateDto);
    }

    /// <summary>
    /// Deletes an invoice (soft delete).
    /// </summary>
    public async Task<bool> DeleteAsync(long id)
    {
        return await DeleteAsync($"/api/invoice/{id}");
    }

    /// <summary>
    /// Restores a soft-deleted invoice back to Draft status.
    /// Calls POST /api/invoice/{id}/restore on the backend.
    /// Only works for invoices that are currently in Deleted status.
    /// </summary>
    /// <param name="id">Invoice ID to restore</param>
    /// <returns>Restored invoice DTO, or null on failure</returns>
    public async Task<InvoiceDto?> RestoreAsync(long id)
    {
        return await PostAsync<object, InvoiceDto>($"/api/invoice/{id}/restore", new { });
    }

    /// <summary>
    /// Reverts a Completed invoice back to Draft for full editing.
    /// </summary>
    public async Task<InvoiceDto?> RevertToDraftAsync(long id)
    {
        return await PostAsync<object, InvoiceDto>($"/api/invoice/{id}/revert-to-draft", new { });
    }

    /// <summary>
    /// Downloads the invoice as a PDF byte array using the default template.
    /// Uses GetBytesAsync from the base class for binary content.
    /// </summary>
    public async Task<byte[]?> ExportToPdfAsync(long id)
    {
        return await GetBytesAsync($"/api/invoice/{id}/pdf");
    }

    /// <summary>
    /// Downloads the invoice as a PDF byte array using a specific content template.
    /// When templateId is null, uses the default template for the document type.
    /// When provided, uses the specified template — allows users to choose PDF layouts.
    /// </summary>
    public async Task<byte[]?> ExportToPdfAsync(long id, long? templateId)
    {
        var url = templateId.HasValue
            ? $"/api/invoice/{id}/pdf?templateId={templateId.Value}"
            : $"/api/invoice/{id}/pdf";
        return await GetBytesAsync(url);
    }

    /// <summary>
    /// Downloads the QR code image (PNG) for the given invoice.
    /// Returns a combined QR Platba+F if the invoice has an IBAN,
    /// or QR Faktura only if no IBAN is available.
    /// </summary>
    /// <param name="id">Invoice ID</param>
    /// <param name="size">QR module size in pixels (5-20, default 10)</param>
    public async Task<byte[]?> GetQrCodeAsync(long id, int size = 10)
    {
        return await GetBytesAsync($"/api/invoice/{id}/qr?size={size}");
    }

    /// <summary>
    /// Completes (issues) a draft invoice — changes status to Completed.
    /// Maps to POST /api/invoice/{id}/complete on the backend.
    /// Uses PostWithoutBodyAsync because the endpoint only needs the invoice ID in the URL.
    /// </summary>
    public async Task<InvoiceDto?> IssueAsync(long id)
    {
        return await PostWithoutBodyAsync<InvoiceDto>($"/api/invoice/{id}/complete");
    }

    /// <summary>
    /// Marks a completed invoice as paid.
    /// Maps to POST /api/invoice/{id}/mark-paid on the backend.
    /// </summary>
    public async Task<InvoiceDto?> MarkAsPaidAsync(long id)
    {
        return await PostWithoutBodyAsync<InvoiceDto>($"/api/invoice/{id}/mark-paid");
    }

    /// <summary>
    /// Reverts a paid invoice back to Completed status (marks it as unpaid).
    /// Removes all linked payment matches so bank transactions become available again.
    /// Maps to POST /api/invoice/{id}/mark-unpaid on the backend.
    /// </summary>
    public async Task<InvoiceDto?> MarkAsUnpaidAsync(long id)
    {
        return await PostWithoutBodyAsync<InvoiceDto>($"/api/invoice/{id}/mark-unpaid");
    }

    /// <summary>
    /// Sends the invoice as an email with PDF attachment to the specified recipient.
    /// The backend generates the PDF and sends it via configured SMTP.
    /// </summary>
    public async Task<bool> SendEmailAsync(long invoiceId, string recipientEmail, string? subject = null, string? message = null)
    {
        var dto = new { RecipientEmail = recipientEmail, Subject = subject, Message = message };
        return await PostBoolAsync($"/api/invoice/{invoiceId}/send-email", dto);
    }

    // ─── Bulk Operations ─────────────────────────────────────────────────────

    /// <summary>
    /// Completes (issues) multiple draft invoices in a single batch via POST /api/invoice/bulk/complete.
    /// Returns a result with success/failure counts and error details for partial failures.
    /// </summary>
    public async Task<BulkOperationResult?> BulkCompleteAsync(List<long> invoiceIds)
    {
        var request = new BulkOperationRequest { InvoiceIds = invoiceIds };
        return await PostAsync<BulkOperationRequest, BulkOperationResult>("/api/invoice/bulk/complete", request);
    }

    /// <summary>
    /// Marks multiple completed invoices as paid in a single batch via POST /api/invoice/bulk/mark-paid.
    /// Returns a result with success/failure counts and error details for partial failures.
    /// </summary>
    public async Task<BulkOperationResult?> BulkMarkAsPaidAsync(List<long> invoiceIds)
    {
        var request = new BulkOperationRequest { InvoiceIds = invoiceIds };
        return await PostAsync<BulkOperationRequest, BulkOperationResult>("/api/invoice/bulk/mark-paid", request);
    }

    /// <summary>
    /// Soft-deletes multiple draft invoices in a single batch via POST /api/invoice/bulk/delete.
    /// Returns a result with success/failure counts and error details for partial failures.
    /// </summary>
    public async Task<BulkOperationResult?> BulkDeleteAsync(List<long> invoiceIds)
    {
        var request = new BulkOperationRequest { InvoiceIds = invoiceIds };
        return await PostAsync<BulkOperationRequest, BulkOperationResult>("/api/invoice/bulk/delete", request);
    }

    /// <summary>
    /// Sends invoice emails for multiple invoices in a single batch via POST /api/invoice/bulk/send-email.
    /// Each email is sent to the invoice's client default email address.
    /// </summary>
    public async Task<BulkOperationResult?> BulkSendEmailAsync(List<long> invoiceIds)
    {
        var request = new BulkOperationRequest { InvoiceIds = invoiceIds };
        return await PostAsync<BulkOperationRequest, BulkOperationResult>("/api/invoice/bulk/send-email", request);
    }

    /// <summary>
    /// Downloads the invoice as an ISDOC 6.0.2 XML byte array.
    /// ISDOC is the Czech electronic invoice standard importable by Pohoda, Money S3, Helios.
    /// Uses GetBytesAsync from the base class for binary content.
    /// </summary>
    public async Task<byte[]?> ExportIsdocAsync(long id)
    {
        return await GetBytesAsync($"/api/invoice/{id}/isdoc");
    }

    /// <summary>
    /// Downloads a ZIP archive containing ISDOC exports for multiple invoices.
    /// Returns the ZIP as a byte array for client-side download via JS interop.
    /// </summary>
    public async Task<byte[]?> BulkExportIsdocAsync(List<long> invoiceIds)
    {
        var idsParam = string.Join(",", invoiceIds);
        return await GetBytesAsync($"/api/invoice/bulk/isdoc?ids={idsParam}");
    }

    /// <summary>
    /// Downloads a ZIP archive containing PDFs for multiple invoices.
    /// Returns the ZIP as a byte array for client-side download via JS interop.
    /// </summary>
    public async Task<byte[]?> BulkExportPdfAsync(List<long> invoiceIds)
    {
        var idsParam = string.Join(",", invoiceIds);
        return await GetBytesAsync($"/api/invoice/bulk/pdf?ids={idsParam}");
    }

    // ─── UBL / Peppol eInvoice export (ADR 0002, F1.6) ───────────────────────

    /// <summary>
    /// Downloads the invoice as a UBL 2.1 / Peppol BIS Billing 3.0 XML file.
    /// Returns <see cref="UblDownloadResult"/> instead of throwing — unlike
    /// <see cref="ExportIsdocAsync"/>, a UBL export can be refused with a structured
    /// "TENANT_NOT_READY" body (EINVOICE_* issues, e.g. "seller has no Peppol ID yet") that the
    /// calling component needs to show the user, not just log and discard.
    /// </summary>
    public async Task<UblDownloadResult> ExportUblAsync(long id, string fallbackFileName)
    {
        var url = $"/api/invoice/{id}/ubl";
        try
        {
            await AddAuthorizationHeaderAsync();
            _logger.LogInformation("GET (UBL) {Url}", url);

            var response = await _httpClient.GetAsync(url);
            if (response.IsSuccessStatusCode)
            {
                var bytes = await response.Content.ReadAsByteArrayAsync();
                return UblDownloadResult.Success(bytes, fallbackFileName);
            }

            return await ParseUblErrorAsync(response);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Unexpected error downloading UBL file from {Url}", url);
            return UblDownloadResult.Failure("UNEXPECTED_ERROR", ex.Message);
        }
    }

    /// <summary>
    /// Downloads a ZIP archive containing UBL exports for multiple invoices.
    /// Same "skip what fails" behavior as <see cref="BulkExportIsdocAsync"/> — the bulk API
    /// endpoint never returns the per-invoice 400 body, so a plain byte array is enough here.
    /// </summary>
    public async Task<byte[]?> BulkExportUblAsync(List<long> invoiceIds)
    {
        var idsParam = string.Join(",", invoiceIds);
        return await GetBytesAsync($"/api/invoice/bulk/ubl?ids={idsParam}");
    }

    // ─── Accounting export (Pohoda / Money S3 / ABRA Flexi) ──────────────────

    /// <summary>
    /// Downloads one XML export file containing issued and/or received invoices for a date range,
    /// formatted for the given accounting system. Used by the AccountingExportDialog shown from
    /// both the Invoices and ReceivedInvoices pages.
    /// </summary>
    public async Task<AccountingExportFile?> ExportAccountingAsync(
        EAccountingSystem system, DateTime from, DateTime to, bool includeIssued, bool includeReceived)
    {
        var request = new AccountingExportRequestDto
        {
            From = from,
            To = to,
            IncludeIssued = includeIssued,
            IncludeReceived = includeReceived
        };
        var response = await PostForBytesAsync($"/api/accounting-export/{system}", request);
        if (response is not { } r) return null;
        // The API leaves out documents the target system cannot represent and reports the count in a header.
        var skipped = r.Headers.TryGetValues("X-Export-Skipped", out var v) && int.TryParse(v.FirstOrDefault(), out var n) ? n : 0;
        return new AccountingExportFile(r.Content, skipped);
    }

    /// <summary>
    /// Reads the JSON error body returned by <c>GET /api/invoice/{id}/ubl</c> for 400/404
    /// responses and maps it to a <see cref="UblDownloadResult"/>.
    /// Expected 400 shape (TenantNotReadyException.ToBadRequestResult):
    /// <c>{ "code": "TENANT_NOT_READY", "message": "...", "issues": [...] }</c>.
    /// </summary>
    private static async Task<UblDownloadResult> ParseUblErrorAsync(HttpResponseMessage response)
    {
        var errorBody = await response.Content.ReadAsStringAsync();

        var code = response.StatusCode == HttpStatusCode.NotFound ? "NOT_FOUND" : "UNKNOWN_ERROR";
        var message = errorBody;
        var issues = new List<ReadinessIssueDto>();

        if (!string.IsNullOrWhiteSpace(errorBody))
        {
            try
            {
                using var doc = JsonDocument.Parse(errorBody);
                if (doc.RootElement.TryGetProperty("code", out var codeProp))
                    code = codeProp.GetString() ?? code;
                if (doc.RootElement.TryGetProperty("message", out var msgProp))
                    message = msgProp.GetString() ?? message;
                if (doc.RootElement.TryGetProperty("issues", out var issuesProp)
                    && issuesProp.ValueKind == JsonValueKind.Array)
                {
                    var parsed = issuesProp.Deserialize<List<ReadinessIssueDto>>(
                        new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
                    if (parsed != null)
                        issues = parsed;
                }
            }
            catch (JsonException)
            {
                // Body was not the expected JSON shape (e.g. an HTML error page from a proxy) —
                // fall through with the raw text as the message rather than throwing here.
            }
        }

        return UblDownloadResult.Failure(code, message, issues);
    }

    // ─── Proforma → Final Invoice (issue #33) ────────────────────────────────

    /// <summary>
    /// Issues a final Invoice from a Proforma via POST /api/invoice/{proformaId}/issue-final.
    /// Appends proportional advance deduction rows automatically.
    /// </summary>
    public async Task<InvoiceDto?> IssueFinalInvoiceAsync(long proformaId, IssueFinalInvoiceDto dto)
    {
        return await PostAsync<IssueFinalInvoiceDto, InvoiceDto>(
            $"/api/invoice/{proformaId}/issue-final", dto);
    }

    /// <summary>
    /// Returns remaining advance (PaidAmount minus already deducted) for a proforma.
    /// Calls GET /api/invoice/{proformaId}/remaining-advance.
    /// </summary>
    public async Task<decimal?> GetRemainingAdvanceAsync(long proformaId)
    {
        return await GetAsync<decimal>($"/api/invoice/{proformaId}/remaining-advance");
    }

    /// <summary>
    /// Returns all final Invoices issued from the given proforma.
    /// Calls GET /api/invoice/{proformaId}/final-invoices.
    /// </summary>
    public async Task<List<InvoiceDto>> GetFinalInvoicesForProformaAsync(long proformaId)
    {
        try
        {
            return await GetAsync<List<InvoiceDto>>($"/api/invoice/{proformaId}/final-invoices") ?? [];
        }
        catch (ApiException)
        {
            return [];
        }
    }

    /// <summary>
    /// Returns all TaxReceiptForAdvance documents linked to the given proforma.
    /// Calls GET /api/invoice/{proformaId}/tax-receipts.
    /// </summary>
    public async Task<List<InvoiceDto>> GetTaxReceiptsForProformaAsync(long proformaId)
    {
        try
        {
            return await GetAsync<List<InvoiceDto>>($"/api/invoice/{proformaId}/tax-receipts") ?? [];
        }
        catch (ApiException)
        {
            return [];
        }
    }

    /// <summary>
    /// Creates a full copy of an invoice as a new Draft document.
    /// Calls POST /api/invoice/{id}/copy on the backend.
    /// Returns the newly created invoice DTO (with the new DocumentNumber),
    /// or null if the copy failed.
    /// </summary>
    public async Task<InvoiceDto?> CopyAsync(long id, bool shiftPeriods = true)
    {
        return await PostWithoutBodyAsync<InvoiceDto>($"/api/invoice/{id}/copy?shiftPeriods={shiftPeriods.ToString().ToLowerInvariant()}");
    }

    // ─── Auto-match ──────────────────────────────────────────────────────────

    /// <summary>
    /// Searches for an auto-match candidate for the given issued invoice.
    /// Returns null (HTTP 204) when no candidate found.
    /// Calls POST /api/invoice/{id}/auto-match.
    /// </summary>
    public Task<AutoMatchProposalDto?> FindAutoMatchAsync(long id) =>
        PostWithoutBodyAsync<AutoMatchProposalDto>($"/api/invoice/{id}/auto-match");
}

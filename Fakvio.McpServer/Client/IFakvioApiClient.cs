using Fakvio.Contracts.Common.Pagination;
using Fakvio.Contracts.Dto.Client;
using Fakvio.Contracts.Dto.Dashboard;
using Fakvio.Contracts.Dto.Email;
using Fakvio.Contracts.Dto.Invoice;
using Fakvio.Contracts.Dto.InvoiceTemplate;
using Fakvio.Contracts.Dto.Readiness;
using Fakvio.Contracts.Dto.ReceivedInvoice;
using Fakvio.Contracts.Dto.Tax;
using Fakvio.Contracts.Dto.VatReport;

namespace Fakvio.McpServer.Client;

/// <summary>
/// Abstraction over the Fakvio REST API.
/// All methods map 1:1 to API controller endpoints.
///
/// Junior note: We use an interface (not a concrete class directly) so that:
///   1. Unit tests can mock this with NSubstitute
///   2. The MCP tool classes don't depend on HTTP details
/// </summary>
public interface IFakvioApiClient
{
    // ── Invoice endpoints ──────────────────────────────────────────────

    /// <summary>GET /api/invoice/paged — paginated invoice list with filters.</summary>
    Task<PagedResult<InvoiceDto>> GetInvoicesPagedAsync(InvoiceFilterDto filter, CancellationToken ct = default);

    /// <summary>GET /api/invoice/{id} — single invoice by ID.</summary>
    Task<InvoiceDto?> GetInvoiceByIdAsync(long id, CancellationToken ct = default);

    /// <summary>GET /api/invoice/by-number/{documentNumber} — find invoice by document number.</summary>
    Task<InvoiceDto?> GetInvoiceByDocumentNumberAsync(string documentNumber, CancellationToken ct = default);

    /// <summary>POST /api/invoice — create a new invoice or credit note.</summary>
    Task<InvoiceDto> CreateInvoiceAsync(CreateInvoiceDto dto, CancellationToken ct = default);

    /// <summary>POST /api/invoice/{id}/complete — issue a draft invoice (generates doc number).</summary>
    Task<InvoiceDto> CompleteInvoiceAsync(long id, CancellationToken ct = default);

    /// <summary>POST /api/invoice/{id}/mark-paid — mark a completed invoice as paid.</summary>
    Task<InvoiceDto> MarkInvoiceAsPaidAsync(long id, CancellationToken ct = default);

    /// <summary>DELETE /api/invoice/{id} — soft-delete a draft invoice.</summary>
    Task DeleteInvoiceAsync(long id, CancellationToken ct = default);

    /// <summary>POST /api/invoice/{id}/send-email — send invoice PDF via email.</summary>
    Task SendInvoiceEmailAsync(long id, SendInvoiceEmailDto dto, CancellationToken ct = default);

    /// <summary>GET /api/invoice/{id}/pdf — export invoice as PDF file (raw bytes).</summary>
    Task<byte[]> ExportInvoicePdfAsync(long id, CancellationToken ct = default);

    /// <summary>GET /api/invoice/{id}/isdoc — export invoice as ISDOC 6.0.2 XML (raw bytes).</summary>
    Task<byte[]> ExportInvoiceIsdocAsync(long id, CancellationToken ct = default);

    // ── Client endpoints ───────────────────────────────────────────────

    /// <summary>GET /api/client/paged — paginated client list with filters.</summary>
    Task<PagedResult<ClientDto>> GetClientsPagedAsync(ClientFilterDto filter, CancellationToken ct = default);

    /// <summary>GET /api/client/{id} — single client by ID.</summary>
    Task<ClientDto?> GetClientByIdAsync(long id, CancellationToken ct = default);

    /// <summary>POST /api/client — create a new client.</summary>
    Task<ClientDto> CreateClientAsync(CreateClientDto dto, CancellationToken ct = default);

    /// <summary>PUT /api/client/{id} — update an existing client.</summary>
    Task<ClientDto> UpdateClientAsync(long id, UpdateClientDto dto, CancellationToken ct = default);

    /// <summary>GET /api/client/ares/{registrationNumber} — ARES registry lookup (Czech companies).</summary>
    Task<ClientDto?> FetchFromAresAsync(string registrationNumber, CancellationToken ct = default);

    /// <summary>GET /api/client/issuer — get the authenticated user's company (issuer).</summary>
    Task<ClientDto?> GetIssuerAsync(CancellationToken ct = default);

    // ── Invoice Template endpoints ─────────────────────────────────────

    /// <summary>GET /api/invoicetemplate/active — active templates, optional doc type filter.</summary>
    Task<List<InvoiceTemplateDto>> GetActiveTemplatesAsync(string? documentType = null, CancellationToken ct = default);

    /// <summary>GET /api/invoicetemplate/{id} — single template by ID.</summary>
    Task<InvoiceTemplateDto?> GetTemplateByIdAsync(long id, CancellationToken ct = default);

    /// <summary>POST /api/invoicetemplate/{id}/create-invoice — create invoice from template.</summary>
    Task<InvoiceDto> CreateInvoiceFromTemplateAsync(long templateId, CreateInvoiceFromTemplateDto dto, CancellationToken ct = default);

    // ── Received Invoice endpoints ──────────────────────────────────────

    /// <summary>GET /api/received-invoice/paged — paginated received invoices with filters.</summary>
    Task<PagedResult<ReceivedInvoiceDto>> GetReceivedInvoicesPagedAsync(ReceivedInvoiceFilterDto filter, CancellationToken ct = default);

    /// <summary>GET /api/received-invoice/{id} — single received invoice by ID.</summary>
    Task<ReceivedInvoiceDto?> GetReceivedInvoiceByIdAsync(long id, CancellationToken ct = default);

    /// <summary>POST /api/received-invoice — create a new received invoice.</summary>
    Task<ReceivedInvoiceDto> CreateReceivedInvoiceAsync(CreateReceivedInvoiceDto dto, CancellationToken ct = default);

    /// <summary>POST /api/received-invoice/{id}/approve — approve for payment.</summary>
    Task<ReceivedInvoiceDto> ApproveReceivedInvoiceAsync(long id, CancellationToken ct = default);

    /// <summary>POST /api/received-invoice/{id}/mark-paid — mark as paid.</summary>
    Task<ReceivedInvoiceDto> MarkReceivedInvoicePaidAsync(long id, CancellationToken ct = default);

    /// <summary>DELETE /api/received-invoice/{id} — soft-delete.</summary>
    Task DeleteReceivedInvoiceAsync(long id, CancellationToken ct = default);

    // ── VAT Report endpoints ─────────────────────────────────────────────

    /// <summary>GET /api/vat-report?from=...&amp;to=... — VAT report for period.</summary>
    Task<VatReportDto> GetVatReportAsync(DateTime from, DateTime to, CancellationToken ct = default);

    // ── Dashboard endpoints ────────────────────────────────────────────

    /// <summary>GET /api/dashboard — tenant dashboard with stats and charts.</summary>
    Task<DashboardDto> GetDashboardAsync(CancellationToken ct = default);

    // ── Tax estimation endpoints ────────────────────────────────────────

    /// <summary>POST /api/tax/estimate — estimate tax obligations for a single regime.</summary>
    Task<TaxEstimationResult> EstimateTaxAsync(TaxEstimationRequest request, CancellationToken ct = default);

    /// <summary>GET /api/tax/compare — compare all applicable regimes for given income.</summary>
    Task<List<TaxEstimationResult>> CompareTaxRegimesAsync(
        decimal grossIncome, string country, int year,
        bool isMainActivity, decimal? actualExpenses,
        CancellationToken ct = default);

    /// <summary>GET /api/tax/income/{year} — annual income from issued invoices.</summary>
    Task<AnnualIncomeDto> GetAnnualIncomeAsync(int year, CancellationToken ct = default);

    /// <summary>GET /api/tax/insurance-advance — upcoming insurance advance payment info.</summary>
    Task<InsuranceAdvanceDto?> GetInsuranceAdvanceAsync(CancellationToken ct = default);

    /// <summary>GET /api/tax/config/{country}/{year} — tax year configuration.</summary>
    Task<TaxYearConfigDto?> GetTaxConfigAsync(string country, int year, CancellationToken ct = default);

    // ── Readiness endpoints ────────────────────────────────────────────

    /// <summary>
    /// GET /api/readiness?issuerId= — what the tenant still has to fill in before invoicing.
    /// An incomplete setup is a normal 200 with issues, not an error; null means the
    /// explicitly requested issuer does not exist in this tenant (404).
    /// </summary>
    Task<ReadinessReportDto?> GetReadinessAsync(long? issuerId = null, CancellationToken ct = default);
}

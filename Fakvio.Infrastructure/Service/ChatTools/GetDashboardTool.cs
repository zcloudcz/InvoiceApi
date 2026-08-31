using System.Text;
using Fakvio.Application.Service;
using Fakvio.Contracts.Dto.Dashboard;
using Microsoft.Extensions.Logging;

namespace Fakvio.Infrastructure.Service.ChatTools;

/// <summary>
/// Chat tool that returns the dashboard summary — the same aggregation the dashboard page shows.
/// Read-only.
///
/// Typical usage:
///   "Jak na tom jsme?"
///   "Kolik mám nezaplacených faktur?"
///   "Show me the dashboard"
///   "Kdo jsou moji největší klienti?"
///
/// Takes no parameters: the aggregation is always "this tenant, right now".
/// </summary>
public class GetDashboardTool : IChatTool
{
    private readonly IDashboardService _dashboardService;
    private readonly ITenantResolver _tenantResolver;
    private readonly ILogger<GetDashboardTool> _logger;

    public GetDashboardTool(
        IDashboardService dashboardService,
        ITenantResolver tenantResolver,
        ILogger<GetDashboardTool> logger)
    {
        _dashboardService = dashboardService;
        _tenantResolver = tenantResolver;
        _logger = logger;
    }

    public string ToolName => "get_dashboard";

    public string Description =>
        "Get the dashboard summary: cashflow due this month, number of clients, unpaid amount, " +
        "overdue invoice count, the most recent invoices, invoice counts per status, " +
        "and the top clients by revenue. Read-only overview — use it for general questions " +
        "about how the business is doing.";

    /// <summary>No parameters — see the class summary.</summary>
    public IReadOnlyList<ChatToolParameter> Parameters => [];

    public async Task<ChatToolResult> ExecuteAsync(
        Dictionary<string, string> parameters,
        CancellationToken ct = default)
    {
        // Scope to the issuer the user is signed in as, exactly like DashboardController does.
        // Null (SysAdmin without impersonation) means "every issuer in this tenant" — the same
        // fallback the REST endpoint has, so chat and the dashboard page never disagree.
        var companyId = _tenantResolver.GetCurrentCompanyId();

        _logger.LogInformation("GetDashboardTool executing for companyId={CompanyId}", companyId);

        var dashboard = await _dashboardService.GetDashboardAsync(companyId, ct);

        return ChatToolResult.Success(Format(dashboard));
    }

    /// <summary>
    /// Renders the dashboard as a compact text block. One fact per line — the model reads this
    /// and answers in one sentence, so anything it cannot quote directly is wasted context.
    /// </summary>
    private static string Format(DashboardDto dashboard)
    {
        var sb = new StringBuilder();
        sb.AppendLine("Dashboard summary:");
        sb.AppendLine($"  Invoices due this month: {dashboard.InvoicesDueThisMonthCount} " +
                      $"(before VAT: {dashboard.InvoicesDueThisMonthTotalWithoutVat:N2}, " +
                      $"with VAT: {dashboard.InvoicesDueThisMonthTotalWithVat:N2})");
        sb.AppendLine($"  Active clients: {dashboard.TotalClients}");
        sb.AppendLine($"  Unpaid (completed, not paid yet): {dashboard.UnpaidAmount:N2}");
        sb.AppendLine($"  Overdue invoices: {dashboard.OverdueInvoicesCount}");

        AppendInvoiceLines(sb, "Recent invoices", dashboard.RecentInvoices);
        AppendInvoiceLines(sb, "Overdue invoices (oldest due date first)", dashboard.OverdueInvoices);

        if (dashboard.InvoiceCountByStatus.Count > 0)
        {
            sb.AppendLine("  Invoice count by status:");
            foreach (var (status, count) in dashboard.InvoiceCountByStatus)
                sb.AppendLine($"    {status}: {count}");
        }

        if (dashboard.InvoiceTotalByClient.Count > 0)
        {
            sb.AppendLine("  Top clients by revenue:");
            foreach (var (clientName, total) in dashboard.InvoiceTotalByClient)
                sb.AppendLine($"    {clientName}: {total:N2}");
        }

        return sb.ToString();
    }

    /// <summary>
    /// Appends one section of invoice rows, or nothing at all when the list is empty —
    /// an empty heading only invites the model to invent rows under it.
    /// </summary>
    private static void AppendInvoiceLines(
        StringBuilder sb,
        string heading,
        List<Contracts.Dto.Invoice.InvoiceDto> invoices)
    {
        if (invoices.Count == 0)
            return;

        sb.AppendLine($"  {heading}:");
        foreach (var invoice in invoices)
        {
            sb.AppendLine(
                $"    ID={invoice.Id} | {invoice.DocumentNumber ?? "(no number)"} | " +
                $"{invoice.ClientName} | {invoice.Status} | " +
                $"due: {ChatToolDates.Format(invoice.DueDate)} | " +
                $"total: {invoice.TotalWithVat:N2} {invoice.CurrencyCode}");
        }
    }
}

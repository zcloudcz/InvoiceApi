using System.ComponentModel;
using System.Text.Json;
using Fakvio.Contracts.Dto.Invoice;
using Fakvio.Contracts.Dto.ReceivedInvoice;
using Fakvio.Domain.Enums;
using Fakvio.McpServer.Client;
using ModelContextProtocol.Server;

namespace Fakvio.McpServer.Tools;

/// <summary>
/// MCP tools for reporting and analytics.
/// Provides dashboard stats, overdue invoice lists, and date-range queries.
///
/// Junior note: These tools are read-only — they don't modify any data.
/// They reuse the same API endpoints but with specific filter combinations
/// that are common in reporting scenarios.
/// </summary>
[McpServerToolType]
public static class ReportingTools
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        WriteIndented = true,
        Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter() }
    };

    /// <summary>
    /// Gets the full dashboard summary with invoice counts, unpaid totals,
    /// overdue invoices, and top clients by revenue.
    /// </summary>
    [McpServerTool(Title = "Get dashboard", ReadOnly = true, Idempotent = true, OpenWorld = false), Description(
        "Get the dashboard summary. Returns: invoices this month, total clients, " +
        "unpaid amount, overdue count, recent invoices, overdue invoices list, " +
        "invoice count by status (for charts), and top clients by revenue.")]
    public static async Task<string> GetDashboard(
        IFakvioApiClient api,
        CancellationToken ct = default)
    {
        try
        {
            var dashboard = await api.GetDashboardAsync(ct);
            return JsonSerializer.Serialize(dashboard, JsonOptions);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            return McpToolError.ToJson(ex);
        }
    }

    /// <summary>
    /// Gets all overdue invoices — completed but unpaid invoices past their due date.
    /// Sorted by due date ascending (most overdue first).
    /// </summary>
    [McpServerTool(Title = "Get overdue invoices", ReadOnly = true, Idempotent = true, OpenWorld = false), Description(
        "Get all overdue invoices (completed but unpaid, past due date). " +
        "Sorted by due date ascending — most overdue first. " +
        "Useful for payment follow-up and collections.")]
    public static async Task<string> GetOverdueInvoices(
        IFakvioApiClient api,
        [Description("Page number (1-based, default 1)")] int page = 1,
        [Description("Items per page (default 50, max 100)")] int pageSize = 50,
        CancellationToken ct = default)
    {
        try
        {
            // Filter for completed (unpaid) invoices that are overdue
            var filter = new InvoiceFilterDto
            {
                Page = page,
                PageSize = Math.Min(pageSize, 100),
                Status = EInvoiceStatus.Completed,
                IsOverdue = true,
                SortBy = "DueDate",
                SortDirection = "asc"
            };

            var result = await api.GetInvoicesPagedAsync(filter, ct);
            return JsonSerializer.Serialize(result, JsonOptions);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            return McpToolError.ToJson(ex);
        }
    }

    /// <summary>
    /// Gets all invoices for a specific client.
    /// Useful for viewing a client's invoice history.
    /// </summary>
    [McpServerTool(Title = "Get client invoices", ReadOnly = true, Idempotent = true, OpenWorld = false), Description(
        "Get all invoices for a specific client. " +
        "Returns the client's invoice history sorted by issue date (newest first). " +
        "Useful for account review and client communication.")]
    public static async Task<string> GetClientInvoices(
        IFakvioApiClient api,
        [Description("The client ID to get invoices for")] long clientId,
        [Description("Page number (1-based, default 1)")] int page = 1,
        [Description("Items per page (default 50, max 100)")] int pageSize = 50,
        CancellationToken ct = default)
    {
        try
        {
            var filter = new InvoiceFilterDto
            {
                Page = page,
                PageSize = Math.Min(pageSize, 100),
                ClientId = clientId,
                SortBy = "IssueDate",
                SortDirection = "desc"
            };

            var result = await api.GetInvoicesPagedAsync(filter, ct);
            return JsonSerializer.Serialize(result, JsonOptions);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            return McpToolError.ToJson(ex);
        }
    }

    /// <summary>
    /// Gets invoices within a specific date range.
    /// Useful for monthly/quarterly reports and accounting summaries.
    /// </summary>
    [McpServerTool(Title = "Get invoices by date range", ReadOnly = true, Idempotent = true, OpenWorld = false), Description(
        "Get invoices within a date range. Useful for monthly/quarterly reports. " +
        "Dates should be ISO 8601 format (e.g., '2026-01-01'). " +
        "Optionally filter by document type or status.")]
    public static async Task<string> GetInvoicesByDateRange(
        IFakvioApiClient api,
        [Description("Start date (ISO 8601, e.g., '2026-01-01')")] string dateFrom,
        [Description("End date (ISO 8601, e.g., '2026-01-31')")] string dateTo,
        [Description("Filter by document type: 'Invoice' or 'CreditNote' (optional)")] string? documentType = null,
        [Description("Filter by status: 'Draft', 'Completed', 'Paid' (optional)")] string? status = null,
        [Description("Page number (1-based, default 1)")] int page = 1,
        [Description("Items per page (default 100, max 100)")] int pageSize = 100,
        CancellationToken ct = default)
    {
        try
        {
            if (!DateTime.TryParse(dateFrom, out var parsedFrom))
                return JsonSerializer.Serialize(new { error = $"Invalid dateFrom format: '{dateFrom}'. Use ISO 8601 (e.g., '2026-01-01')." }, JsonOptions);

            if (!DateTime.TryParse(dateTo, out var parsedTo))
                return JsonSerializer.Serialize(new { error = $"Invalid dateTo format: '{dateTo}'. Use ISO 8601 (e.g., '2026-01-31')." }, JsonOptions);

            var filter = new InvoiceFilterDto
            {
                Page = page,
                PageSize = Math.Min(pageSize, 100),
                IssueDateFrom = parsedFrom,
                IssueDateTo = parsedTo,
                SortBy = "IssueDate",
                SortDirection = "asc"
            };

            // Parse optional enum filters
            if (!string.IsNullOrEmpty(documentType) && Enum.TryParse<EDocumentType>(documentType, ignoreCase: true, out var dt))
                filter.DocumentType = dt;

            if (!string.IsNullOrEmpty(status) && Enum.TryParse<EInvoiceStatus>(status, ignoreCase: true, out var st))
                filter.Status = st;

            var result = await api.GetInvoicesPagedAsync(filter, ct);
            return JsonSerializer.Serialize(result, JsonOptions);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            return McpToolError.ToJson(ex);
        }
    }

    /// <summary>
    /// Gets a VAT (DPH) report for a specified period.
    /// Shows output VAT, input VAT, tax liability, revenue, expenses, and profit.
    /// </summary>
    [McpServerTool(Title = "Get VAT report", ReadOnly = true, Idempotent = true, OpenWorld = false), Description(
        "Get a VAT (DPH) report for a date period. Shows output VAT (from issued invoices), " +
        "input VAT (from received invoices), tax liability (output - input), " +
        "revenue, expenses, and profit. Uses TaxableSupplyDate (DUZP). " +
        "Dates should be ISO 8601 format (e.g., '2026-01-01').")]
    public static async Task<string> GetVatReport(
        IFakvioApiClient api,
        [Description("Period start date (ISO 8601, e.g., '2026-01-01')")] string dateFrom,
        [Description("Period end date (ISO 8601, e.g., '2026-03-31')")] string dateTo,
        CancellationToken ct = default)
    {
        try
        {
            if (!DateTime.TryParse(dateFrom, out var parsedFrom))
                return JsonSerializer.Serialize(new { error = $"Invalid dateFrom format: '{dateFrom}'. Use ISO 8601." }, JsonOptions);

            if (!DateTime.TryParse(dateTo, out var parsedTo))
                return JsonSerializer.Serialize(new { error = $"Invalid dateTo format: '{dateTo}'. Use ISO 8601." }, JsonOptions);

            var result = await api.GetVatReportAsync(parsedFrom, parsedTo, ct);
            return JsonSerializer.Serialize(result, JsonOptions);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            return McpToolError.ToJson(ex);
        }
    }

    /// <summary>
    /// Exports an EPO XML filing (VAT return, control statement or EU summary statement)
    /// for one period, returned as a base64 file like the other export tools.
    /// </summary>
    [McpServerTool(Title = "Export VAT filing for EPO", ReadOnly = true, Idempotent = true, OpenWorld = false), Description(
        "Export an XML filing for the Czech tax portal EPO for one period. kind: " +
        "'return' = DPHDP3 VAT return (Priznani k DPH), 'control' = DPHKH1 control statement (Kontrolni hlaseni), " +
        "'summary' = DPHSHV EU summary statement (Souhrnne hlaseni, supplies to EU VAT payers). " +
        "Returns the XML as a base64-encoded string. Requires a VAT-payer company with EPO tax office settings. " +
        "For 'summary', supplies default to services (code 3); pass goodsVatIds to report those customers as goods (code 0).")]
    public static async Task<string> ExportVatEpo(
        IFakvioApiClient api,
        [Description("Filing kind: 'return', 'control' or 'summary'")] string kind,
        [Description("Tax year, e.g. 2026")] int year,
        [Description("Period number: 1-12 for monthly, 1-4 for quarterly")] int period,
        [Description("'Monthly' (default) or 'Quarterly'")] string periodType = "Monthly",
        [Description("Summary only: customer VAT ids (country prefix + number, e.g. 'DE123456789') to report as goods instead of services")] string[]? goodsVatIds = null,
        CancellationToken ct = default)
    {
        try
        {
            var (route, prefix) = kind?.Trim().ToLowerInvariant() switch
            {
                "return"  => ("epo/return", "DPHDP3"),
                "control" => ("epo/control-statement", "DPHKH1"),
                "summary" => ("epo/summary-statement", "DPHSHV"),
                _ => (null, null)
            };
            if (route is null)
                return JsonSerializer.Serialize(new { error = $"Invalid kind '{kind}'. Use 'return', 'control' or 'summary'." }, JsonOptions);

            if (!Enum.TryParse<EVatPeriodType>(periodType, ignoreCase: true, out var parsedType))
                return JsonSerializer.Serialize(new { error = $"Invalid periodType '{periodType}'. Use 'Monthly' or 'Quarterly'." }, JsonOptions);

            var bytes = await api.ExportVatEpoAsync(route, year, period, parsedType.ToString(), goodsVatIds, ct);
            var part = parsedType == EVatPeriodType.Monthly ? $"M{period:D2}" : $"Q{period}";

            return JsonSerializer.Serialize(new
            {
                success = true,
                fileName = $"{prefix}_{year}_{part}.xml",
                mimeType = "application/xml",
                sizeBytes = bytes.Length,
                base64Content = Convert.ToBase64String(bytes)
            }, JsonOptions);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            return McpToolError.ToJson(ex);
        }
    }

    /// <summary>
    /// Gets overdue received invoices — approved but unpaid expenses past due date.
    /// </summary>
    [McpServerTool(Title = "Get overdue received invoices", ReadOnly = true, Idempotent = true, OpenWorld = false), Description(
        "Get overdue received (incoming) invoices — approved but unpaid expenses past due date. " +
        "Useful for tracking outstanding supplier payments.")]
    public static async Task<string> GetOverdueReceivedInvoices(
        IFakvioApiClient api,
        [Description("Page number (1-based, default 1)")] int page = 1,
        [Description("Items per page (default 50, max 100)")] int pageSize = 50,
        CancellationToken ct = default)
    {
        try
        {
            var filter = new ReceivedInvoiceFilterDto
            {
                Page = page,
                PageSize = Math.Min(pageSize, 100),
                Status = EReceivedInvoiceStatus.Approved,
                IsOverdue = true,
                SortBy = "DueDate",
                SortDirection = "asc"
            };

            var result = await api.GetReceivedInvoicesPagedAsync(filter, ct);
            return JsonSerializer.Serialize(result, JsonOptions);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            return McpToolError.ToJson(ex);
        }
    }
}

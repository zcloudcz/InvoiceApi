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
/// Junior note: These tools are read-only â they don't modify any data.
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
    [McpServerTool, Description(
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
        catch (OperationCanceledException)
        {
            // Cancellation is not a domain error — propagate it instead of
            // swallowing it into a fake "error" JSON result (issue #279).
            throw;
        }
        catch (Exception ex)
        {
            return McpToolError.ToJson(ex);
        }
    }

    /// <summary>
    /// Gets all overdue invoices â completed but unpaid invoices past their due date.
    /// Sorted by due date ascending (most overdue first).
    /// </summary>
    [McpServerTool, Description(
        "Get all overdue invoices (completed but unpaid, past due date). " +
        "Sorted by due date ascending â most overdue first. " +
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
        catch (OperationCanceledException)
        {
            // Cancellation is not a domain error — propagate it instead of
            // swallowing it into a fake "error" JSON result (issue #279).
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
    [McpServerTool, Description(
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
        catch (OperationCanceledException)
        {
            // Cancellation is not a domain error — propagate it instead of
            // swallowing it into a fake "error" JSON result (issue #279).
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
    [McpServerTool, Description(
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
        catch (OperationCanceledException)
        {
            // Cancellation is not a domain error — propagate it instead of
            // swallowing it into a fake "error" JSON result (issue #279).
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
    [McpServerTool, Description(
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
        catch (OperationCanceledException)
        {
            // Cancellation is not a domain error — propagate it instead of
            // swallowing it into a fake "error" JSON result (issue #279).
            throw;
        }
        catch (Exception ex)
        {
            return McpToolError.ToJson(ex);
        }
    }

    /// <summary>
    /// Gets overdue received invoices â approved but unpaid expenses past due date.
    /// </summary>
    [McpServerTool, Description(
        "Get overdue received (incoming) invoices â approved but unpaid expenses past due date. " +
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
        catch (OperationCanceledException)
        {
            // Cancellation is not a domain error — propagate it instead of
            // swallowing it into a fake "error" JSON result (issue #279).
            throw;
        }
        catch (Exception ex)
        {
            return McpToolError.ToJson(ex);
        }
    }
}

using System.ComponentModel;
using System.Text.Json;
using Fakvio.Contracts.Dto.ReceivedInvoice;
using Fakvio.Domain.Enums;
using Fakvio.McpServer.Client;
using ModelContextProtocol.Server;

namespace Fakvio.McpServer.Tools;

/// <summary>
/// MCP tools for managing received (incoming) invoices â expenses from suppliers.
/// Provides CRUD operations and status transitions (Received -> Approved -> Paid).
///
/// Junior note: These tools mirror the ReceivedInvoiceController endpoints.
/// Each tool calls IFakvioApiClient methods and returns JSON results.
/// </summary>
[McpServerToolType]
public static class ReceivedInvoiceTools
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        WriteIndented = true,
        Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter() }
    };

    /// <summary>
    /// Lists received invoices with pagination and filtering.
    /// </summary>
    [McpServerTool, Description(
        "List received (incoming) invoices with pagination and filters. " +
        "These are expenses from suppliers. " +
        "Filter by status ('Received', 'Approved', 'Paid', 'Rejected'), supplier ID, date range, etc.")]
    public static async Task<string> ListReceivedInvoices(
        IFakvioApiClient api,
        [Description("Page number (1-based, default 1)")] int page = 1,
        [Description("Items per page (default 50, max 100)")] int pageSize = 50,
        [Description("Search in document number, supplier name, variable symbol")] string? search = null,
        [Description("Filter by status: 'Received', 'Approved', 'Paid', 'Rejected'")] string? status = null,
        [Description("Filter by supplier (client) ID")] long? supplierId = null,
        [Description("Filter from issue date (ISO 8601)")] string? issueDateFrom = null,
        [Description("Filter to issue date (ISO 8601)")] string? issueDateTo = null,
        [Description("Show only overdue (unpaid past due date)")] bool? isOverdue = null,
        CancellationToken ct = default)
    {
        try
        {
            var filter = new ReceivedInvoiceFilterDto
            {
                Page = page,
                PageSize = Math.Min(pageSize, 100),
                Search = search,
                SupplierId = supplierId,
                IsOverdue = isOverdue,
                SortBy = "ReceivedDate",
                SortDirection = "desc"
            };

            if (!string.IsNullOrEmpty(status) &&
                Enum.TryParse<EReceivedInvoiceStatus>(status, ignoreCase: true, out var st))
                filter.Status = st;

            if (!string.IsNullOrEmpty(issueDateFrom) && DateTime.TryParse(issueDateFrom, out var from))
                filter.IssueDateFrom = from;

            if (!string.IsNullOrEmpty(issueDateTo) && DateTime.TryParse(issueDateTo, out var to))
                filter.IssueDateTo = to;

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

    /// <summary>
    /// Gets a single received invoice by ID.
    /// </summary>
    [McpServerTool, Description(
        "Get a received (incoming) invoice by ID. Returns full details including line items and supplier info.")]
    public static async Task<string> GetReceivedInvoice(
        IFakvioApiClient api,
        [Description("The received invoice ID")] long id,
        CancellationToken ct = default)
    {
        try
        {
            var result = await api.GetReceivedInvoiceByIdAsync(id, ct);
            if (result is null)
                return JsonSerializer.Serialize(new { error = $"Received invoice with ID {id} not found." }, JsonOptions);

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
    /// Creates a new received invoice.
    /// </summary>
    [McpServerTool, Description(
        "Create a new received (incoming) invoice. Pass JSON with: " +
        "supplierId (required), currencyId (required), items[] (required, at least 1), " +
        "documentNumber, issueDate, receivedDate, dueDate, taxableSupplyDate, " +
        "variableSymbol, paymentMethod, bankAccountNumber, iban, swift, notes. " +
        "Each item needs: description, quantity, unitPrice, vatRatePercentage (or vatRateId).")]
    public static async Task<string> CreateReceivedInvoice(
        IFakvioApiClient api,
        [Description("JSON string of CreateReceivedInvoiceDto")] string invoiceJson,
        CancellationToken ct = default)
    {
        try
        {
            var dto = JsonSerializer.Deserialize<CreateReceivedInvoiceDto>(invoiceJson, JsonOptions);
            if (dto is null)
                return JsonSerializer.Serialize(new { error = "Failed to parse invoice JSON." }, JsonOptions);

            var result = await api.CreateReceivedInvoiceAsync(dto, ct);
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
    /// Approves a received invoice for payment.
    /// </summary>
    [McpServerTool, Description(
        "Approve a received invoice for payment. Transitions from 'Received' to 'Approved'. " +
        "Only invoices in 'Received' status can be approved.")]
    public static async Task<string> ApproveReceivedInvoice(
        IFakvioApiClient api,
        [Description("The received invoice ID to approve")] long id,
        CancellationToken ct = default)
    {
        try
        {
            var result = await api.ApproveReceivedInvoiceAsync(id, ct);
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
    /// Marks a received invoice as paid.
    /// </summary>
    [McpServerTool, Description(
        "Mark a received invoice as paid. Transitions from 'Approved' to 'Paid'. " +
        "Only invoices in 'Approved' status can be marked as paid.")]
    public static async Task<string> MarkReceivedInvoicePaid(
        IFakvioApiClient api,
        [Description("The received invoice ID to mark as paid")] long id,
        CancellationToken ct = default)
    {
        try
        {
            var result = await api.MarkReceivedInvoicePaidAsync(id, ct);
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
    /// Deletes a received invoice (soft delete).
    /// </summary>
    [McpServerTool, Description(
        "Delete a received invoice (soft delete). Only 'Received' or 'Rejected' invoices can be deleted.")]
    public static async Task<string> DeleteReceivedInvoice(
        IFakvioApiClient api,
        [Description("The received invoice ID to delete")] long id,
        CancellationToken ct = default)
    {
        try
        {
            await api.DeleteReceivedInvoiceAsync(id, ct);
            return JsonSerializer.Serialize(new { success = true, message = $"Received invoice {id} deleted." }, JsonOptions);
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

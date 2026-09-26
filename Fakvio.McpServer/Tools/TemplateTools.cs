using System.ComponentModel;
using System.Text.Json;
using Fakvio.Contracts.Dto.InvoiceTemplate;
using Fakvio.McpServer.Client;
using ModelContextProtocol.Server;

namespace Fakvio.McpServer.Tools;

/// <summary>
/// MCP tools for working with invoice templates.
/// Templates are reusable blueprints for creating invoices quickly.
///
/// Junior note: A template stores default items, payment terms, etc.
/// When you "create invoice from template", it clones those defaults
/// into a new draft invoice for a specific client.
/// </summary>
[McpServerToolType]
public static class TemplateTools
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        WriteIndented = true,
        Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter() }
    };

    /// <summary>
    /// Lists all active invoice templates, optionally filtered by document type.
    /// </summary>
    [McpServerTool(Title = "List templates", ReadOnly = true, Idempotent = true, OpenWorld = false), Description(
        "List all active invoice templates. Optionally filter by document type. " +
        "Templates are reusable blueprints containing default items, payment terms, and settings.")]
    public static async Task<string> ListTemplates(
        IFakvioApiClient api,
        [Description("Filter by document type: 'Invoice' or 'CreditNote' (optional)")] string? documentType = null,
        CancellationToken ct = default)
    {
        try
        {
            var templates = await api.GetActiveTemplatesAsync(documentType, ct);
            return JsonSerializer.Serialize(templates, JsonOptions);
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
    /// Gets a single template by ID with all details including line items.
    /// </summary>
    [McpServerTool(Title = "Get template", ReadOnly = true, Idempotent = true, OpenWorld = false), Description(
        "Get a single invoice template by ID. Returns full details including " +
        "default line items, payment terms, currency, and usage statistics.")]
    public static async Task<string> GetTemplate(
        IFakvioApiClient api,
        [Description("The template ID (database primary key)")] long templateId,
        CancellationToken ct = default)
    {
        try
        {
            var template = await api.GetTemplateByIdAsync(templateId, ct);

            if (template is null)
                return JsonSerializer.Serialize(new { error = $"Template with ID {templateId} not found." }, JsonOptions);

            return JsonSerializer.Serialize(template, JsonOptions);
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
    /// Creates a new invoice from an existing template.
    /// Clones the template's items and settings, then applies the specified client and dates.
    /// </summary>
    [McpServerTool(Title = "Create invoice from template", ReadOnly = false, Destructive = false, Idempotent = false, OpenWorld = false), Description(
        "Create a new invoice from a template. Clones the template's items and settings " +
        "into a new draft invoice for the specified client. " +
        "Requires templateId and clientId. Optional: issueDate, dueDate, variableSymbol, " +
        "autoComplete (set true to immediately issue the invoice).")]
    public static async Task<string> CreateInvoiceFromTemplate(
        IFakvioApiClient api,
        [Description("The template ID to use as blueprint")] long templateId,
        [Description("Creation options — clientId is required, everything else overrides a template default")]
        CreateInvoiceFromTemplateDto options,
        CancellationToken ct = default)
    {
        if (options is null)
            return JsonSerializer.Serialize(new { error = "options is required." }, JsonOptions);

        try
        {
            var result = await api.CreateInvoiceFromTemplateAsync(templateId, options, ct);
            return JsonSerializer.Serialize(result, JsonOptions);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (TenantNotReadyApiException ex)
        {
            // Only reachable with autoComplete = true (#342) — same structured payload as
            // InvoiceTools.CompleteInvoice, so the MCP client reads the fix route either way.
            // Bypassing McpToolError.ToJson (#279) is deliberate for the same reason as there:
            // this is our own parsed readiness contract, not a raw API error body.
            return JsonSerializer.Serialize(new
            {
                error = ex.Message,
                code = TenantNotReadyApiException.ErrorCode,
                missingFields = ex.MissingFields,
                issues = ex.Issues
            }, JsonOptions);
        }
        catch (Exception ex)
        {
            return McpToolError.ToJson(ex);
        }
    }
}

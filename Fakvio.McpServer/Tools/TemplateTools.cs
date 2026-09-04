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
    [McpServerTool, Description(
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
    /// Gets a single template by ID with all details including line items.
    /// </summary>
    [McpServerTool, Description(
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
    /// Creates a new invoice from an existing template.
    /// Clones the template's items and settings, then applies the specified client and dates.
    /// </summary>
    [McpServerTool, Description(
        "Create a new invoice from a template. Clones the template's items and settings " +
        "into a new draft invoice for the specified client. " +
        "Requires templateId and clientId. Optional: issueDate, dueDate, variableSymbol, " +
        "autoComplete (set true to immediately issue the invoice).")]
    public static async Task<string> CreateInvoiceFromTemplate(
        IFakvioApiClient api,
        [Description("The template ID to use as blueprint")] long templateId,
        [Description(
            "JSON string with creation options. Example: " +
            "{\"clientId\":5,\"autoComplete\":false,\"notes\":\"Monthly hosting\"}"
        )] string optionsJson,
        CancellationToken ct = default)
    {
        try
        {
            var dto = JsonSerializer.Deserialize<CreateInvoiceFromTemplateDto>(optionsJson, JsonOptions);

            if (dto is null)
                return JsonSerializer.Serialize(new { error = "Invalid JSON: could not deserialize creation options." }, JsonOptions);

            var result = await api.CreateInvoiceFromTemplateAsync(templateId, dto, ct);
            return JsonSerializer.Serialize(result, JsonOptions);
        }
        catch (JsonException ex)
        {
            return JsonSerializer.Serialize(new { error = $"Invalid JSON format: {ex.Message}" }, JsonOptions);
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

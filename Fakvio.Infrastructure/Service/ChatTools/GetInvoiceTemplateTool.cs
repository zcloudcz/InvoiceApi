using Fakvio.Application.Service;
using Microsoft.Extensions.Logging;

namespace Fakvio.Infrastructure.Service.ChatTools;

/// <summary>
/// Chat tool that reads one invoice template in full, including its line items (issue #225).
///
/// Typical usage:
///   "Co je v šabloně Měsíční hosting?"
///   "Jaké položky má šablona 5?"
///
/// Read-only. The template is addressed by ID only — the model gets the IDs from
/// <see cref="ListInvoiceTemplatesTool"/>, which can also search by name, so a name-based
/// lookup here would only duplicate that search with worse disambiguation.
/// </summary>
public class GetInvoiceTemplateTool : IChatTool
{
    private readonly IInvoiceTemplateService _templateService;
    private readonly ILogger<GetInvoiceTemplateTool> _logger;

    public GetInvoiceTemplateTool(
        IInvoiceTemplateService templateService,
        ILogger<GetInvoiceTemplateTool> logger)
    {
        _templateService = templateService;
        _logger = logger;
    }

    public string ToolName => "get_invoice_template";

    public string Description =>
        "Get one invoice template by ID with all details: pre-filled line items, currency, " +
        "payment details, number sequence and usage statistics. " +
        "Call list_invoice_templates first to find the ID.";

    /// <summary>Parameter schema — static because it never changes per instance.</summary>
    private static readonly ChatToolParameter[] Schema =
    [
        new()
        {
            Name = "id",
            Type = ChatToolParameterType.Integer,
            Description = "Invoice template ID, as shown by list_invoice_templates",
            IsRequired = true
        }
    ];

    public IReadOnlyList<ChatToolParameter> Parameters => Schema;

    public async Task<ChatToolResult> ExecuteAsync(
        Dictionary<string, string> parameters,
        CancellationToken ct = default)
    {
        var templateId = TemplateChatToolSupport.RequiredId(parameters, "id");

        _logger.LogInformation("GetInvoiceTemplateTool executing for template {TemplateId}", templateId);

        var template = await _templateService.GetTemplateByIdAsync(templateId, ct);

        return template is null
            ? ChatToolResult.Failure(
                $"There is no invoice template with ID {templateId}. " +
                "Use list_invoice_templates to see the existing templates and their IDs.")
            : ChatToolResult.Success(TemplateChatToolSupport.FormatInvoiceTemplate(template));
    }
}

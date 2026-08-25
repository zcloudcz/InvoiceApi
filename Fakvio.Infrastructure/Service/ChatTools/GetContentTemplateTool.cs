using Fakvio.Application.Service;
using Microsoft.Extensions.Logging;

namespace Fakvio.Infrastructure.Service.ChatTools;

/// <summary>
/// Chat tool that reads one content template — a PDF or e-mail HTML template (issue #225).
///
/// Typical usage:
///   "Co je v šabloně e-mailu k faktuře?"
///   "Je šablona 3 výchozí?"
///
/// Read-only, and it returns the template's METADATA plus the e-mail subject — never the HTML
/// body itself, only its size. Editing the HTML by conversation is out of scope (story #149,
/// question 3: the visual editor is the better tool), and a whole template is tens of kilobytes
/// that the conversation would then carry in every following message.
/// </summary>
public class GetContentTemplateTool : IChatTool
{
    private readonly IContentTemplateService _contentTemplateService;
    private readonly ILogger<GetContentTemplateTool> _logger;

    public GetContentTemplateTool(
        IContentTemplateService contentTemplateService,
        ILogger<GetContentTemplateTool> logger)
    {
        _contentTemplateService = contentTemplateService;
        _logger = logger;
    }

    public string ToolName => "get_content_template";

    public string Description =>
        "Get one content template (PDF or e-mail HTML template) by ID: name, type, language, " +
        "whether it is the default, the e-mail subject and the size of the HTML body. " +
        "The HTML itself is not returned and cannot be edited through chat — the user edits it " +
        "in the visual editor. Call list_content_templates first to find the ID.";

    /// <summary>Parameter schema — static because it never changes per instance.</summary>
    private static readonly ChatToolParameter[] Schema =
    [
        new()
        {
            Name = "id",
            Type = ChatToolParameterType.Integer,
            Description = "Content template ID, as shown by list_content_templates",
            IsRequired = true
        }
    ];

    public IReadOnlyList<ChatToolParameter> Parameters => Schema;

    public async Task<ChatToolResult> ExecuteAsync(
        Dictionary<string, string> parameters,
        CancellationToken ct = default)
    {
        var templateId = TemplateChatToolSupport.RequiredId(parameters, "id");

        _logger.LogInformation("GetContentTemplateTool executing for template {TemplateId}", templateId);

        var template = await _contentTemplateService.GetByIdAsync(templateId, ct);

        return template is null
            ? ChatToolResult.Failure(
                $"There is no content template with ID {templateId}. " +
                "Use list_content_templates to see the existing templates and their IDs.")
            : ChatToolResult.Success(TemplateChatToolSupport.FormatContentTemplate(template));
    }
}

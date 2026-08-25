using System.Text;
using Fakvio.Application.Service;
using Fakvio.Contracts.Dto.ContentTemplate;
using Fakvio.Domain.Enums;
using Microsoft.Extensions.Logging;

namespace Fakvio.Infrastructure.Service.ChatTools;

/// <summary>
/// Chat tool that lists content templates — the HTML that renders PDF documents and e-mail
/// bodies (issue #225).
///
/// Typical usage:
///   "Jaké mám šablony dokumentů?"
///   "Která šablona faktury je výchozí?"
///   "Ukaž anglické e-mailové šablony"
///
/// Read-only. It is the tool that hands the model the template IDs that
/// <see cref="GetContentTemplateTool"/> and <see cref="SetDefaultContentTemplateTool"/> need.
///
/// No paging on purpose: the list is bounded by the number of template types times the
/// languages a tenant uses — a handful of rows, not a data set.
/// </summary>
public class ListContentTemplatesTool : IChatTool
{
    private readonly IContentTemplateService _contentTemplateService;
    private readonly ILogger<ListContentTemplatesTool> _logger;

    public ListContentTemplatesTool(
        IContentTemplateService contentTemplateService,
        ILogger<ListContentTemplatesTool> logger)
    {
        _contentTemplateService = contentTemplateService;
        _logger = logger;
    }

    public string ToolName => "list_content_templates";

    public string Description =>
        "List content templates — the HTML templates that render PDF documents (invoice, credit " +
        "note, reminder, ...) and e-mail bodies. Shows which one is the default for each type " +
        "and language. Use set_default_content_template to change the default, and " +
        "list_invoice_templates for the separate invoice DATA blueprints.";

    /// <summary>
    /// Parameter schema — static because it never changes per instance. Every filter is
    /// optional: with no parameters the tool returns all active templates.
    ///
    /// The allowed types are read from <see cref="EContentTemplateType"/> rather than typed
    /// out, so a newly added template type cannot silently become invisible to the assistant.
    /// </summary>
    private static readonly ChatToolParameter[] Schema =
    [
        new()
        {
            Name = "template_type",
            Type = ChatToolParameterType.String,
            Description = "Filter by template type (PDF outputs end with Pdf, e-mail bodies with Email)",
            AllowedValues = Enum.GetNames<EContentTemplateType>()
        },
        new()
        {
            Name = "language",
            Type = ChatToolParameterType.String,
            Description = "Filter by template language — ISO 639-1 code, e.g. cs or en"
        },
        new()
        {
            Name = "include_inactive",
            Type = ChatToolParameterType.Boolean,
            Description = "True to include deactivated templates as well (default false)"
        }
    ];

    public IReadOnlyList<ChatToolParameter> Parameters => Schema;

    public async Task<ChatToolResult> ExecuteAsync(
        Dictionary<string, string> parameters,
        CancellationToken ct = default)
    {
        _logger.LogInformation("ListContentTemplatesTool executing with parameters: {Params}",
            string.Join(", ", parameters.Keys));

        var templateType = TemplateChatToolSupport.OptionalEnum<EContentTemplateType>(parameters, "template_type");
        var includeInactive = TemplateChatToolSupport.OptionalFlag(parameters, "include_inactive") ?? false;

        var templates = templateType is { } type
            ? await _contentTemplateService.GetAllByTypeAsync(type, includeInactive, ct)
            : await _contentTemplateService.GetAllAsync(includeInactive, ct);

        // The language filter is applied here, not in the service: IContentTemplateService has
        // no language-filtered list, and the result set is a handful of rows anyway.
        var language = TemplateChatToolSupport.OptionalText(parameters, "language");
        if (language is not null)
        {
            templates = templates
                .Where(template => string.Equals(template.Language, language, StringComparison.OrdinalIgnoreCase))
                .ToList();
        }

        return ChatToolResult.Success(Format(templates));
    }

    private static string Format(IReadOnlyList<ContentTemplateDto> templates)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"Content templates (total: {templates.Count}):");

        if (templates.Count == 0)
        {
            sb.AppendLine("  No content templates match the specified filters.");
            return sb.ToString();
        }

        foreach (var template in templates)
        {
            sb.AppendLine($"  {TemplateChatToolSupport.DescribeContentTemplate(template)}");
        }

        return sb.ToString();
    }
}

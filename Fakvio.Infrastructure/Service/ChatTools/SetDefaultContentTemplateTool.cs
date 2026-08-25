using Fakvio.Application.Service;
using Fakvio.Contracts.Dto.ContentTemplate;
using Microsoft.Extensions.Logging;

namespace Fakvio.Infrastructure.Service.ChatTools;

/// <summary>
/// Chat tool that makes one content template the default for its type and language (issue #225).
///
/// Typical usage:
///   "Nastav šablonu 4 jako výchozí pro faktury"
///   "Používej pro e-maily k faktuře radši tu druhou šablonu"
///
/// This is the only write among the template tools, so it is an <see cref="IConfirmableChatTool"/>:
/// the first call only previews what would change, and nothing is written until the user approves
/// and the model repeats the call with <c>confirm: true</c>. See DEVGUIDE §4.7.
///
/// The default is scoped by (template type, language) — <c>ContentTemplateService</c> unsets the
/// previous default of that exact pair — so switching the Czech invoice e-mail leaves the English
/// one alone.
/// </summary>
public class SetDefaultContentTemplateTool : IConfirmableChatTool
{
    private readonly IContentTemplateService _contentTemplateService;
    private readonly ILogger<SetDefaultContentTemplateTool> _logger;

    public SetDefaultContentTemplateTool(
        IContentTemplateService contentTemplateService,
        ILogger<SetDefaultContentTemplateTool> logger)
    {
        _contentTemplateService = contentTemplateService;
        _logger = logger;
    }

    public string ToolName => "set_default_content_template";

    public string Description =>
        "Make one content template the default for its type and language, so new PDFs and " +
        "e-mails of that type use it. The previous default for the same type and language is " +
        "unset automatically. Call list_content_templates first to find the ID. " +
        "Invoice DATA templates (list_invoice_templates) have no default — this tool does not " +
        "apply to them.";

    /// <summary>Parameter schema — static because it never changes per instance.</summary>
    private static readonly ChatToolParameter[] Schema =
    [
        new()
        {
            Name = "id",
            Type = ChatToolParameterType.Integer,
            Description = "Content template ID that should become the default, as shown by list_content_templates",
            IsRequired = true
        }
    ];

    public IReadOnlyList<ChatToolParameter> Parameters => Schema;

    /// <summary>
    /// Describes which template would replace which, without touching anything.
    /// </summary>
    public async Task<ChatToolResult> BuildPreviewAsync(
        Dictionary<string, string> parameters,
        CancellationToken ct = default)
    {
        var templateId = TemplateChatToolSupport.RequiredId(parameters, "id");

        var (template, currentDefault, error) = await ResolveAsync(templateId, ct);
        if (error is not null)
            return ChatToolResult.Failure(error);

        var replaced = currentDefault is null
            ? $"There is no default template for {template!.TemplateType} in language " +
              $"'{template.Language}' yet."
            : $"It would replace the current default '{currentDefault.Name}' (ID {currentDefault.Id}).";

        return ChatToolResult.Success(
            $"'{template!.Name}' (ID {template.Id}) would become the default template for " +
            $"{template.TemplateType} in language '{template.Language}'. {replaced}");
    }

    /// <summary>
    /// Applies the change. The template is re-read here rather than carried over from the
    /// preview: the two calls are not paired, so the stored data may have moved on since
    /// (see <see cref="IConfirmableChatTool"/>).
    /// </summary>
    public async Task<ChatToolResult> ExecuteAsync(
        Dictionary<string, string> parameters,
        CancellationToken ct = default)
    {
        var templateId = TemplateChatToolSupport.RequiredId(parameters, "id");

        _logger.LogInformation("SetDefaultContentTemplateTool executing for template {TemplateId}", templateId);

        var (template, _, error) = await ResolveAsync(templateId, ct);
        if (error is not null)
            return ChatToolResult.Failure(error);

        // Only the flag is sent — every other field stays as it is, and ContentTemplateService
        // unsets the previous default of the same (type, language) pair by itself.
        var updated = await _contentTemplateService.UpdateAsync(
            templateId,
            new UpdateContentTemplateDto { IsDefault = true },
            ct);

        if (updated is null)
            return ChatToolResult.Failure(
                $"The content template (ID {templateId}) could not be updated because it no longer exists.");

        _logger.LogInformation(
            "Content template {TemplateId} is now the default for {TemplateType}/{Language}",
            templateId, template!.TemplateType, template.Language);

        return ChatToolResult.Success(
            $"'{updated.Name}' is now the default template for {updated.TemplateType} in " +
            $"language '{updated.Language}'.\n{TemplateChatToolSupport.FormatContentTemplate(updated)}");
    }

    /// <summary>
    /// Loads the template and the default it would replace, or explains why the change makes no
    /// sense. One method for both the preview and the write, so the preview can never promise
    /// something the write then refuses.
    /// </summary>
    private async Task<(ContentTemplateDto? Template, ContentTemplateDto? CurrentDefault, string? Error)> ResolveAsync(
        long templateId,
        CancellationToken ct)
    {
        var template = await _contentTemplateService.GetByIdAsync(templateId, ct);

        if (template is null)
            return (null, null, $"There is no content template with ID {templateId}. " +
                                "Use list_content_templates to see the existing templates and their IDs.");

        // An inactive template can hold the flag in the database, but no document would ever use
        // it — every default lookup filters on IsActive. Setting it would report a change that
        // has no effect, so it is refused instead.
        if (!template.IsActive)
            return (null, null,
                $"'{template.Name}' (ID {template.Id}) is deactivated, so it cannot become the " +
                $"default — documents only ever use active templates. The user has to activate " +
                $"it first at {TemplateChatToolSupport.ContentTemplateEditRoute}.");

        if (template.IsDefault)
            return (null, null,
                $"'{template.Name}' (ID {template.Id}) already is the default template for " +
                $"{template.TemplateType} in language '{template.Language}'. Nothing to change.");

        // Inactive templates are included on purpose: an inactive row can still carry IsDefault,
        // and it is exactly the row the update would unset — so the preview must name it.
        var siblings = await _contentTemplateService.GetAllByTypeAsync(
            template.TemplateType, includeInactive: true, ct);

        var currentDefault = siblings.FirstOrDefault(sibling =>
            sibling.IsDefault &&
            string.Equals(sibling.Language, template.Language, StringComparison.OrdinalIgnoreCase));

        return (template, currentDefault, null);
    }
}

using System.Text;
using Fakvio.Application.Service;
using Fakvio.Contracts.Common.Pagination;
using Fakvio.Contracts.Dto.InvoiceTemplate;
using Fakvio.Domain.Enums;
using Microsoft.Extensions.Logging;

namespace Fakvio.Infrastructure.Service.ChatTools;

/// <summary>
/// Chat tool that lists invoice templates — the blueprints of invoice DATA (issue #225).
///
/// Typical usage:
///   "Jaké mám šablony faktur?"
///   "Ukaž šablony pro dobropisy"
///   "Máme šablonu na hosting?"
///
/// Read-only. It is the tool that hands the model the template IDs that
/// <see cref="GetInvoiceTemplateTool"/> needs.
/// </summary>
public class ListInvoiceTemplatesTool : IChatTool
{
    private readonly IInvoiceTemplateService _templateService;
    private readonly ILogger<ListInvoiceTemplatesTool> _logger;

    public ListInvoiceTemplatesTool(
        IInvoiceTemplateService templateService,
        ILogger<ListInvoiceTemplatesTool> logger)
    {
        _templateService = templateService;
        _logger = logger;
    }

    public string ToolName => "list_invoice_templates";

    public string Description =>
        "List invoice templates — reusable blueprints of invoice DATA (line items, currency, " +
        "payment details) used to create an invoice quickly. Returns paged results with the " +
        "template IDs; use get_invoice_template for the full detail of one template. " +
        "For the HTML that renders a PDF or an e-mail, use list_content_templates instead.";

    /// <summary>Items per page when the model does not ask for a specific size.</summary>
    private const int DefaultPageSize = 10;

    /// <summary>
    /// Upper bound on page size. The model pays for every row in its context window, and a
    /// list is a lead-in to <c>get_invoice_template</c>, not the answer itself.
    /// </summary>
    private const int MaxPageSize = 50;

    /// <summary>
    /// Parameter schema — static because it never changes per instance.
    /// Everything is optional: with no parameters the tool returns the first page of active
    /// templates.
    ///
    /// The allowed document types are read from <see cref="EDocumentType"/> rather than typed
    /// out, so a new document type cannot silently become invisible to the assistant.
    /// </summary>
    private static readonly ChatToolParameter[] Schema =
    [
        new()
        {
            Name = "page",
            Type = ChatToolParameterType.Integer,
            Description = "Page number (default 1)"
        },
        new()
        {
            Name = "page_size",
            Type = ChatToolParameterType.Integer,
            Description = $"Items per page (default {DefaultPageSize}, max {MaxPageSize})"
        },
        new()
        {
            Name = "search",
            Type = ChatToolParameterType.String,
            Description = "Search in template name, description and category"
        },
        new()
        {
            Name = "document_type",
            Type = ChatToolParameterType.String,
            Description = "Filter by the document type the template produces",
            AllowedValues = Enum.GetNames<EDocumentType>()
        },
        new()
        {
            Name = "category",
            Type = ChatToolParameterType.String,
            Description = "Filter by category (e.g. Hosting, Consulting)"
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
        _logger.LogInformation("ListInvoiceTemplatesTool executing with parameters: {Params}",
            string.Join(", ", parameters.Keys));

        // includeInactive=true means "do not filter on IsActive at all", hence the null.
        var includeInactive = TemplateChatToolSupport.OptionalFlag(parameters, "include_inactive") ?? false;

        var page = await _templateService.GetTemplatesPagedAsync(
            page: TemplateChatToolSupport.OptionalCount(parameters, "page", 1),
            pageSize: Math.Min(
                TemplateChatToolSupport.OptionalCount(parameters, "page_size", DefaultPageSize),
                MaxPageSize),
            search: TemplateChatToolSupport.OptionalText(parameters, "search"),
            documentType: TemplateChatToolSupport.OptionalEnum<EDocumentType>(parameters, "document_type"),
            category: TemplateChatToolSupport.OptionalText(parameters, "category"),
            isActive: includeInactive ? null : true,
            cancellationToken: ct);

        return ChatToolResult.Success(FormatPage(page));
    }

    /// <summary>
    /// Formats the paged result: one compact line per template, plus enough paging information
    /// that the model knows whether asking for page 2 makes sense.
    /// </summary>
    private static string FormatPage(PagedResult<InvoiceTemplateDto> page)
    {
        // PagedResult.TotalPages is 0 for an empty result; the model reads "page 1/1" better.
        var totalPages = Math.Max(1, page.TotalPages);

        var sb = new StringBuilder();
        sb.AppendLine($"Invoice templates (page {page.PageNumber}/{totalPages}, total: {page.TotalCount}):");

        if (page.Items.Count == 0)
        {
            sb.AppendLine("  No invoice templates match the specified filters.");
            return sb.ToString();
        }

        foreach (var template in page.Items)
        {
            sb.AppendLine($"  {TemplateChatToolSupport.DescribeInvoiceTemplate(template)}");
        }

        return sb.ToString();
    }
}

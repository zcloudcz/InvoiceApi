using System.Text;
using Fakvio.Application.Service;
using Fakvio.Contracts.Common.Pagination;
using Fakvio.Contracts.Dto.Client;
using Microsoft.Extensions.Logging;

namespace Fakvio.Infrastructure.Service.ChatTools;

/// <summary>
/// Chat tool that returns a paged list of clients with optional filtering.
/// Answers questions like:
///   "Ukaž mi všechny klienty"
///   "Kteří klienti jsou plátci DPH?"
///   "Najdi klienta Alfa"
///   "Jaká je moje firma?"  → is_issuer = true
///
/// It is also the chat equivalent of the MCP <c>GetIssuer</c> tool: the issuer is just the
/// one client row with <c>IsIssuer = true</c>, so <c>is_issuer=true</c> returns it. A separate
/// tool would add a row to the catalog the model reads on every single message, for a filter
/// this one already has — the same reasoning that kept the reporting tools at three
/// (see DEVGUIDE §4.7).
/// </summary>
public class ListClientsTool : IChatTool
{
    private readonly IClientService _clientService;
    private readonly ILogger<ListClientsTool> _logger;

    public ListClientsTool(IClientService clientService, ILogger<ListClientsTool> logger)
    {
        _clientService = clientService;
        _logger = logger;
    }

    public string ToolName => "list_clients";

    public string Description =>
        "List clients (customers) with optional search and filtering by VAT status. " +
        "Set is_issuer=true to get the user's own company (the issuer) instead of customers. " +
        "Returns paged results; use get_client for the full detail of one client.";

    /// <summary>Items per page when the model does not ask for a specific size.</summary>
    private const int DefaultPageSize = 10;

    /// <summary>
    /// Upper bound on page size. The model pays for every row in its context window, and a
    /// list is a lead-in to <c>get_client</c>, not the answer itself.
    /// </summary>
    private const int MaxPageSize = 50;

    /// <summary>
    /// Parameter schema — static because it never changes per instance.
    /// Every filter is optional: with no parameters the tool returns the first page
    /// of active customers.
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
            Description = "Search in company name, trading name, registration number (IČO) or city"
        },
        new()
        {
            Name = "is_vat_payer",
            Type = ChatToolParameterType.Boolean,
            Description = "True to return only VAT payers, false only non-payers"
        },
        new()
        {
            Name = "is_issuer",
            Type = ChatToolParameterType.Boolean,
            Description = "True to return the user's own company (the issuer) instead of customers"
        },
        new()
        {
            Name = "include_inactive",
            Type = ChatToolParameterType.Boolean,
            Description = "True to include deleted (inactive) clients (default false)"
        }
    ];

    public IReadOnlyList<ChatToolParameter> Parameters => Schema;

    public async Task<ChatToolResult> ExecuteAsync(
        Dictionary<string, string> parameters,
        CancellationToken ct = default)
    {
        _logger.LogInformation("ListClientsTool executing with parameters: {Params}",
            string.Join(", ", parameters.Select(kv => $"{kv.Key}={kv.Value}")));

        var filter = new ClientFilterDto
        {
            Page = ParseInt(parameters, "page", 1),
            PageSize = Math.Min(ParseInt(parameters, "page_size", DefaultPageSize), MaxPageSize),
            Search = ParseText(parameters, "search"),
            IsVatPayer = ParseBool(parameters, "is_vat_payer"),
            IsIssuer = ParseBool(parameters, "is_issuer"),
            IncludeInactive = ParseBool(parameters, "include_inactive") ?? false
        };

        var page = await _clientService.GetClientsPagedAsync(filter, ct);

        return ChatToolResult.Success(FormatPage(page));
    }

    /// <summary>
    /// Formats the paged result into a text block for the AI to present:
    /// one compact line per client, plus enough paging information that the model
    /// knows whether asking for page 2 makes sense.
    /// </summary>
    private static string FormatPage(PagedResult<ClientDto> page)
    {
        // PagedResult.TotalPages is 0 for an empty result; the model reads "page 1/1" better.
        var totalPages = Math.Max(1, page.TotalPages);

        var sb = new StringBuilder();
        sb.AppendLine($"Clients (page {page.PageNumber}/{totalPages}, total: {page.TotalCount}):");

        if (page.Items.Count == 0)
        {
            sb.AppendLine("  No clients match the specified filters.");
            return sb.ToString();
        }

        foreach (var client in page.Items)
        {
            sb.AppendLine($"  {ClientLookup.Describe(client)}");
        }

        return sb.ToString();
    }

    // ─── Parsing helpers ─────────────────────────────────────────────────────
    // Types are validated centrally by ChatToolExecutor before the tool runs, so these only
    // translate "not supplied" into the service's default — they are not a second validation.

    private static int ParseInt(Dictionary<string, string> parameters, string key, int defaultValue)
        => parameters.TryGetValue(key, out var raw) && int.TryParse(raw, out var value) && value > 0
            ? value
            : defaultValue;

    private static bool? ParseBool(Dictionary<string, string> parameters, string key)
        => parameters.TryGetValue(key, out var raw) && bool.TryParse(raw, out var value)
            ? value
            : null;

    private static string? ParseText(Dictionary<string, string> parameters, string key)
        => parameters.TryGetValue(key, out var raw) && !string.IsNullOrWhiteSpace(raw)
            ? raw.Trim()
            : null;
}

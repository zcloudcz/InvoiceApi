using Fakvio.Application.Service;
using Fakvio.Contracts.Dto.Chat;
using Fakvio.Contracts.Dto.Client;
using Microsoft.Extensions.Logging;

namespace Fakvio.Infrastructure.Service.ChatTools;

/// <summary>
/// Chat tool that handles navigation requests within the Fakvio application.
/// When a user says "Open new invoice for client ABC" or "Show me client XYZ",
/// this tool:
/// 1. Identifies the target page (invoice, client, etc.)
/// 2. Resolves entity references (finds client by name → gets ID)
/// 3. Returns a ChatToolResult with a UiAction that the Blazor client executes
///
/// Supported navigation targets:
/// - new_invoice:      /invoices/create (optionally with ?clientId=X)
/// - new_credit_note:  /invoices/create?type=CreditNote (optionally with ?clientId=X)
/// - client_detail:    /clients/{id} (requires client_name to resolve ID)
/// - client_list:      /clients
/// - invoice_list:     /invoices
/// - new_client:       /clients/create
///
/// Junior note: This tool doesn't navigate directly — it returns a ChatUiAction
/// that the Blazor client (ChatPanel) interprets and executes via NavigationManager.
/// The server can't call NavigationManager because it runs in a different process.
/// </summary>
public class NavigateTool : IChatTool
{
    private readonly IClientService _clientService;
    private readonly ILogger<NavigateTool> _logger;

    public NavigateTool(IClientService clientService, ILogger<NavigateTool> logger)
    {
        _clientService = clientService;
        _logger = logger;
    }

    public string ToolName => "navigate";

    public string Description =>
        "Navigates the user to a page in the application. " +
        "Can open new invoice, new credit note, show client detail, " +
        "open client list, invoice list, or new client form. " +
        "If a client name is mentioned, it finds the client first. " +
        "Opens forms only — it never creates a document.";

    /// <summary>
    /// Parameter schema — static because it never changes per instance.
    /// AllowedValues make the model pick a real target instead of inventing one;
    /// unknown targets are rejected centrally before this tool runs.
    /// </summary>
    private static readonly ChatToolParameter[] Schema =
    [
        new()
        {
            Name = "target",
            Type = ChatToolParameterType.String,
            Description = "Where to navigate in the application",
            IsRequired = true,
            AllowedValues =
            [
                "new_invoice", "new_credit_note", "client_detail",
                "client_list", "invoice_list", "new_client"
            ]
        },
        new()
        {
            Name = "client_name",
            Type = ChatToolParameterType.String,
            Description = "Client/company name — required for client_detail, optional for " +
                          "new_invoice and new_credit_note to pre-select the client"
        }
    ];

    public IReadOnlyList<ChatToolParameter> Parameters => Schema;

    public async Task<ChatToolResult> ExecuteAsync(
        Dictionary<string, string> parameters,
        CancellationToken ct = default)
    {
        // Presence and allowed value of "target" are guaranteed by ChatToolExecutor's
        // central validation — the switch below only maps a known target to a route.
        var target = parameters["target"].Trim().ToLowerInvariant();

        // Extract optional client_name for client resolution.
        parameters.TryGetValue("client_name", out var clientName);
        clientName = clientName?.Trim();

        _logger.LogInformation("NavigateTool executing: target={Target}, client_name={ClientName}",
            target, clientName ?? "(none)");

        return target switch
        {
            "new_invoice" => await HandleNewInvoice(clientName, ct),
            "new_credit_note" => await HandleNewCreditNote(clientName, ct),
            "client_detail" => await HandleClientDetail(clientName, ct),
            "client_list" => SuccessNav("Opening client list.", "/clients"),
            "invoice_list" => SuccessNav("Opening invoice list.", "/invoices"),
            "new_client" => SuccessNav("Opening new client form.", "/clients/create"),
            _ => ChatToolResult.Failure(
                $"Unknown target: '{target}'. Must be one of: " +
                "new_invoice, new_credit_note, client_detail, client_list, invoice_list, new_client.")
        };
    }

    // ─── Private helpers ─────────────────────────────────────────────────

    /// <summary>
    /// Handles "new_invoice" target — optionally with client pre-selection.
    /// </summary>
    private async Task<ChatToolResult> HandleNewInvoice(string? clientName, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(clientName))
        {
            return SuccessNav("Opening new invoice form.", "/invoices/create");
        }

        var clientResult = await ResolveClientAsync(clientName, ct);
        if (clientResult.Error != null)
            return clientResult.Error;

        return SuccessNav(
            $"Opening new invoice for client '{clientResult.Client!.CompanyName}'.",
            $"/invoices/create?clientId={clientResult.Client.Id}");
    }

    /// <summary>
    /// Handles "new_credit_note" target — optionally with client pre-selection.
    /// </summary>
    private async Task<ChatToolResult> HandleNewCreditNote(string? clientName, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(clientName))
        {
            return SuccessNav("Opening new credit note form.", "/invoices/create?type=CreditNote");
        }

        var clientResult = await ResolveClientAsync(clientName, ct);
        if (clientResult.Error != null)
            return clientResult.Error;

        return SuccessNav(
            $"Opening new credit note for client '{clientResult.Client!.CompanyName}'.",
            $"/invoices/create?type=CreditNote&clientId={clientResult.Client.Id}");
    }

    /// <summary>
    /// Handles "client_detail" target — requires client name to resolve ID.
    /// </summary>
    private async Task<ChatToolResult> HandleClientDetail(string? clientName, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(clientName))
        {
            return ChatToolResult.Failure(
                "Missing 'client_name' parameter. Please specify which client to show.");
        }

        var clientResult = await ResolveClientAsync(clientName, ct);
        if (clientResult.Error != null)
            return clientResult.Error;

        return SuccessNav(
            $"Opening detail for client '{clientResult.Client!.CompanyName}' (ID: {clientResult.Client.Id}).",
            $"/clients/{clientResult.Client.Id}");
    }

    /// <summary>
    /// Searches for a client by name using GetClientsPagedAsync.
    /// Returns the single matching client, or an error result if 0 or multiple matches.
    ///
    /// Why PageSize=5? We only need to know if there's exactly 1 match.
    /// If there are more, we show the top matches for disambiguation.
    /// </summary>
    private async Task<ClientResolveResult> ResolveClientAsync(string clientName, CancellationToken ct)
    {
        var filter = new ClientFilterDto
        {
            Search = clientName,
            PageSize = 5,
            IsIssuer = false // Only search customers, not the issuer (your own company).
        };

        var result = await _clientService.GetClientsPagedAsync(filter, ct);

        if (result.TotalCount == 0)
        {
            _logger.LogInformation("NavigateTool: no client found for '{ClientName}'", clientName);
            return new ClientResolveResult
            {
                Error = ChatToolResult.Failure(
                    $"No client found matching '{clientName}'. " +
                    "Please check the name or use a different search term.")
            };
        }

        if (result.TotalCount == 1)
        {
            var client = result.Items[0];
            _logger.LogInformation("NavigateTool: resolved '{ClientName}' to client ID {ClientId} ({CompanyName})",
                clientName, client.Id, client.CompanyName);
            return new ClientResolveResult { Client = client };
        }

        // Multiple matches — list them for the user to clarify.
        _logger.LogInformation("NavigateTool: '{ClientName}' matched {Count} clients",
            clientName, result.TotalCount);

        var matchList = string.Join("\n",
            result.Items.Select(c =>
                $"  - {c.CompanyName} (ID: {c.Id}" +
                (string.IsNullOrEmpty(c.RegistrationNumber) ? ")" : $", IČO: {c.RegistrationNumber})")));

        return new ClientResolveResult
        {
            Error = ChatToolResult.Success(
                $"Found {result.TotalCount} clients matching '{clientName}':\n{matchList}\n\n" +
                "Please specify which client you mean (use a more specific name or IČO).")
        };
    }

    /// <summary>
    /// Shorthand for creating a successful result with a navigate action.
    /// </summary>
    private static ChatToolResult SuccessNav(string outputText, string url)
        => ChatToolResult.SuccessWithAction(outputText, ChatUiAction.Navigate(url));

    /// <summary>
    /// Internal helper record for client resolution result.
    /// Either Client is set (single match) or Error is set (0 or multiple matches).
    /// </summary>
    private record ClientResolveResult
    {
        public ClientDto? Client { get; init; }
        public ChatToolResult? Error { get; init; }
    }
}

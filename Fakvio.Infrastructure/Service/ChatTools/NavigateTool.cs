using Fakvio.Application.Service;
using Fakvio.Contracts.Dto.Chat;
using Fakvio.Contracts.Dto.Client;
using Microsoft.Extensions.Logging;

namespace Fakvio.Infrastructure.Service.ChatTools;

/// <summary>
/// Chat tool that handles navigation requests within the Fakvio application.
/// When a user says "Open new invoice for client ABC" or "Show me the VAT report",
/// this tool:
/// 1. Maps the requested target onto an application route
/// 2. Resolves entity references where needed (finds client by name → gets ID)
/// 3. Returns a ChatToolResult with a UiAction that the Blazor client executes
///
/// The offered targets are the <see cref="Routes"/> catalog below plus
/// <c>client_detail</c>. The catalog covers every tenant-facing page of the app;
/// auth-flow pages (/login, /register, …) and SysAdmin-only pages (/logs,
/// /system-settings, /companies, /sysadmin/*, …) are deliberately left out — the
/// assistant runs in a tenant user's session and must not send them somewhere they
/// would only get an "access denied".
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
        "Navigates the user to a page in the application — documents, clients, payments, " +
        "templates, taxes, reminders or settings. " +
        "If a client name is mentioned, it finds the client first. " +
        "Opens pages and forms only — it never creates a document.";

    /// <summary>
    /// One navigation target: the value the model sends, the URL the Blazor client opens,
    /// and a label used in the sentence shown back to the user ("Opening {label}.").
    /// </summary>
    /// <param name="Target">Enum value offered to the model, snake_case.</param>
    /// <param name="Url">Application route, exactly as declared by the page's <c>@page</c>.</param>
    /// <param name="Label">Lower-case noun phrase describing the page.</param>
    private sealed record NavigationRoute(string Target, string Url, string Label);

    /// <summary>
    /// Target that has no fixed URL — the page id comes from resolving a client by name,
    /// so it is handled separately in <see cref="ExecuteAsync"/>.
    /// </summary>
    private const string ClientDetailTarget = "client_detail";

    /// <summary>
    /// The two document forms that accept an optional client pre-selection
    /// (<c>?clientId=…</c>); every other target ignores <c>client_name</c>.
    /// </summary>
    private const string NewInvoiceTarget = "new_invoice";
    private const string NewCreditNoteTarget = "new_credit_note";

    /// <summary>
    /// Catalog of every tenant-facing route the assistant may open, grouped by area.
    ///
    /// This is the single source of truth: the allowed values advertised to the model and
    /// the URL the client navigates to both come from here, so a target can never be
    /// offered without a route (or vice versa). Order matters only cosmetically — the
    /// generated prompt example uses the first value, hence <c>new_invoice</c> first.
    ///
    /// Routes with a path parameter (/invoices/{id}, /payments/{id}, …) are NOT here:
    /// they need an entity resolved first, and only client resolution exists today.
    /// </summary>
    private static readonly NavigationRoute[] Routes =
    [
        // Issued documents
        new(NewInvoiceTarget, "/invoices/create", "new invoice form"),
        new(NewCreditNoteTarget, "/invoices/create?type=CreditNote", "new credit note form"),
        new("invoice_list", "/invoices", "invoice list"),
        new("invoice_import", "/invoices/import", "invoice import"),

        // Received documents
        new("new_received_invoice", "/received-invoices/create", "new received invoice form"),
        new("received_invoice_list", "/received-invoices", "received invoice list"),
        new("received_invoice_import", "/received-invoices/import", "received invoice import"),
        new("invoice_emails", "/invoice-emails", "inbound invoice e-mails"),

        // Clients
        new("new_client", "/clients/create", "new client form"),
        new("client_list", "/clients", "client list"),

        // Payments
        new("payment_list", "/payments", "payment list"),

        // Templates
        new("invoice_template_list", "/invoice-templates", "invoice template list"),
        new("new_invoice_template", "/invoice-templates/create", "new invoice template form"),
        new("content_template_list", "/content-templates", "content template list"),
        new("new_content_template", "/content-templates/create", "new content template form"),

        // Taxes and VAT
        new("vat_report", "/vat-report", "VAT report"),
        new("oss_report", "/oss-report", "EU OSS report"),
        new("vat_rate_list", "/vat-rates", "VAT rate list"),
        new("tax_estimation", "/tax-estimation", "tax estimation"),
        new("tax_year_configs", "/tax-configs", "tax year configuration"),

        // Reminders (dunning)
        new("reminder_list", "/reminders", "reminder list"),
        new("reminder_settings", "/reminders/settings", "reminder settings"),

        // Company and user settings
        new("dashboard", "/", "dashboard"),
        new("my_company", "/my-company", "my company page"),
        new("setup_wizard", "/setup", "guided setup wizard (company, bank, invoicing)"),
        new("number_sequences", "/number-sequences", "number sequence settings"),
        new("feedback", "/feedback", "my feedback reports"),
        new("preferences", "/preferences", "user preferences"),
        new("two_factor_settings", "/profile/two-factor", "two-factor authentication settings"),
        new("notifications", "/notifications", "notification list"),
        new("integrations", "/settings/integrations", "integrations and API keys for AI clients"),
        new("webhooks", "/settings/webhooks", "outbound webhooks (admin only)")
    ];

    /// <summary>Lookup built once from <see cref="Routes"/> — the switch below is a dictionary hit.</summary>
    private static readonly Dictionary<string, NavigationRoute> RouteByTarget =
        Routes.ToDictionary(route => route.Target, StringComparer.Ordinal);

    /// <summary>
    /// Everything the model may send as <c>target</c>. Derived from the catalog so the
    /// enum and the routing table cannot drift apart; <c>client_detail</c> is appended
    /// because it is the one target resolved from a name instead of a fixed URL.
    /// </summary>
    private static readonly string[] AllowedTargets =
        [.. Routes.Select(route => route.Target), ClientDetailTarget];

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
            Description = "Which page of the application to open",
            IsRequired = true,
            AllowedValues = AllowedTargets
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
        // central validation — the code below only maps a known target to a route.
        var target = parameters["target"].Trim().ToLowerInvariant();

        // Extract optional client_name for client resolution.
        parameters.TryGetValue("client_name", out var clientName);
        clientName = clientName?.Trim();

        _logger.LogInformation("NavigateTool executing: target={Target}, client_name={ClientName}",
            target, clientName ?? "(none)");

        if (target == ClientDetailTarget)
            return await HandleClientDetail(clientName, ct);

        if (!RouteByTarget.TryGetValue(target, out var route))
        {
            // Unreachable through the executor (it rejects values outside AllowedTargets),
            // but the tool is also called directly, so it answers instead of throwing.
            return ChatToolResult.Failure(
                $"Unknown target: '{target}'. Must be one of: {string.Join(", ", AllowedTargets)}.");
        }

        // Only the two document forms understand ?clientId= — anywhere else the name is
        // just extra context the model volunteered and there is nothing to resolve.
        var preselectsClient = target is NewInvoiceTarget or NewCreditNoteTarget;
        if (!preselectsClient || string.IsNullOrWhiteSpace(clientName))
            return SuccessNav($"Opening {route.Label}.", route.Url);

        var clientResult = await ResolveClientAsync(clientName, ct);
        if (clientResult.Error != null)
            return clientResult.Error;

        // The credit note route already carries ?type=CreditNote, the invoice route carries
        // nothing — pick the separator from the URL instead of hard-coding it per target.
        var separator = route.Url.Contains('?') ? '&' : '?';

        return SuccessNav(
            $"Opening {route.Label} for client '{clientResult.Client!.CompanyName}'.",
            $"{route.Url}{separator}clientId={clientResult.Client.Id}");
    }

    // ─── Private helpers ─────────────────────────────────────────────────

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

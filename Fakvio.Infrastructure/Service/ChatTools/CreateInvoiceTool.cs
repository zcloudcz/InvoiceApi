using System.Globalization;
using System.Text.Json;
using Fakvio.Application.Service;
using Fakvio.Contracts.Dto.Chat;
using Fakvio.Contracts.Dto.Client;
using Fakvio.Contracts.Dto.Currency;
using Fakvio.Contracts.Dto.Invoice;
using Fakvio.Domain.Enums;
using Microsoft.Extensions.Logging;

namespace Fakvio.Infrastructure.Service.ChatTools;

/// <summary>
/// Chat tool that creates a real invoice in the database via IInvoiceService.
///
/// When a user says "Vytvoř fakturu pro Alza za mléko na 999,-", this tool:
/// 1. Resolves the client by name (searches customers, not issuers)
/// 2. Gets the issuer (your company) from IClientService.GetIssuerAsync()
/// 3. Gets the default currency (CZK) and default VAT rate
/// 4. Parses item descriptions/prices from the AI-provided parameters
/// 5. Calls IInvoiceService.CreateInvoiceAsync() to create the actual invoice
/// 6. Returns a success result with a navigate action to the invoice detail page
///
/// Parameters from AI:
///   - client_name (string, required): Name or partial name of the client
///   - items (string, required): JSON array of line items, e.g.:
///     [{"description": "Mléko", "quantity": 1, "unit_price": 999}]
///   - currency (string, optional): Currency code (e.g., "EUR"). Defaults to "CZK".
///   - notes (string, optional): Invoice notes
///
/// Junior note: This is a write operation — it actually creates an invoice in the database.
/// The tool validates all required data before calling CreateInvoiceAsync to prevent
/// half-created invoices or cryptic EF Core errors.
/// </summary>
public class CreateInvoiceTool : IChatTool
{
    private readonly IInvoiceService _invoiceService;
    private readonly IClientService _clientService;
    private readonly ICurrencyService _currencyService;
    private readonly IVatRateService _vatRateService;
    private readonly IReverseChargeCodeService _reverseChargeCodeService;
    private readonly ILogger<CreateInvoiceTool> _logger;

    public CreateInvoiceTool(
        IInvoiceService invoiceService,
        IClientService clientService,
        ICurrencyService currencyService,
        IVatRateService vatRateService,
        IReverseChargeCodeService reverseChargeCodeService,
        ILogger<CreateInvoiceTool> logger)
    {
        _invoiceService = invoiceService;
        _clientService = clientService;
        _currencyService = currencyService;
        _vatRateService = vatRateService;
        _reverseChargeCodeService = reverseChargeCodeService;
        _logger = logger;
    }

    public string ToolName => "create_invoice";

    public string Description =>
        "Creates a new invoice for a client with specified items. " +
        "Automatically resolves the client by name, sets default currency (CZK), " +
        "applies default VAT rate, and generates a document number. " +
        "After creation, navigates to the invoice detail page. " +
        "Use this when the user provides item details (description, price) — " +
        "when they only want to open the form, use 'navigate' with target new_invoice instead.";

    /// <summary>
    /// Parameter schema — static because it never changes per instance.
    /// </summary>
    private static readonly ChatToolParameter[] Schema =
    [
        new()
        {
            Name = "client_name",
            Type = ChatToolParameterType.String,
            Description = "Name of the client/company to invoice",
            IsRequired = true
        },
        new()
        {
            Name = "items",
            Type = ChatToolParameterType.ObjectArray,
            Description = "Invoice line items. Each item has \"description\" (string, required), " +
                          "\"quantity\" (number, default 1), \"unit_price\" (number, required), " +
                          "optional \"vat_regime\" (one of \"Standard\" (default), \"ReverseCharge\", " +
                          "\"Exempt\", \"OutOfScope\") and optional \"reverse_charge_code\" (MFCR code " +
                          "string, e.g. \"4\" for construction work — required when vat_regime is " +
                          "\"ReverseCharge\", §92a-92e ZDPH). " +
                          "Example: [{\"description\": \"Web development\", \"quantity\": 10, \"unit_price\": 1500}]",
            IsRequired = true
        },
        new()
        {
            Name = "currency",
            Type = ChatToolParameterType.String,
            Description = "ISO 4217 currency code (CZK, EUR, …). Default: CZK"
        },
        new()
        {
            Name = "notes",
            Type = ChatToolParameterType.String,
            Description = "Optional notes to include on the invoice"
        },
        new()
        {
            Name = "bank_account_id",
            Type = ChatToolParameterType.Integer,
            Description = "Optional: one of the issuer's bank account ids to use instead of the " +
                          "automatic default (see get_my_company for the account list)."
        }
    ];

    public IReadOnlyList<ChatToolParameter> Parameters => Schema;

    /// <summary>
    /// Creates an invoice by resolving all required references and calling IInvoiceService.
    /// The flow is:
    /// 1. Read parameters (validated centrally by ChatToolExecutor)
    /// 2. Resolve client by name → get ClientId
    /// 3. Get issuer → get IssuerId
    /// 4. Get currency (default CZK) → get CurrencyId
    /// 5. Get default VAT rate → get VatRateId
    /// 6. Parse items JSON → build CreateInvoiceItemDto list
    /// 7. Call CreateInvoiceAsync → get created invoice
    /// 8. Return SuccessWithAction (navigate to invoice detail)
    /// </summary>
    public async Task<ChatToolResult> ExecuteAsync(
        Dictionary<string, string> parameters,
        CancellationToken ct = default)
    {
        // ── Step 1: Read parameters ──────────────────────────────────────
        // Required parameters are guaranteed present and well-formed (items is a JSON array)
        // by ChatToolExecutor's central validation.

        var clientName = parameters["client_name"];
        var itemsJson = parameters["items"];

        // Optional parameters.
        parameters.TryGetValue("currency", out var currencyCode);
        parameters.TryGetValue("notes", out var notes);
        long? bankAccountId = null;
        if (parameters.TryGetValue("bank_account_id", out var bankAccountIdRaw)
            && !string.IsNullOrWhiteSpace(bankAccountIdRaw))
        {
            // Present but not a number: fail loudly instead of silently using the default account.
            if (!long.TryParse(bankAccountIdRaw, out var parsedBankAccountId))
                return ChatToolResult.Failure("bank_account_id must be a number");
            bankAccountId = parsedBankAccountId;
        }

        _logger.LogInformation(
            "CreateInvoiceTool executing: client_name={ClientName}, items={Items}, currency={Currency}",
            clientName, itemsJson, currencyCode ?? "CZK");

        // ── Step 2: Resolve client by name ───────────────────────────────

        var clientResult = await ResolveClientAsync(clientName.Trim(), ct);
        if (clientResult.Error != null)
            return clientResult.Error;

        var client = clientResult.Client!;

        // ── Step 3: Get issuer (your company) ────────────────────────────

        var issuer = await _clientService.GetIssuerAsync(ct);
        if (issuer == null)
        {
            _logger.LogWarning("CreateInvoiceTool: no issuer configured");
            return ChatToolResult.Failure(
                "No issuer (your company) is configured. " +
                "Please set up your company first in Settings.");
        }

        // ── Step 4: Get currency ─────────────────────────────────────────

        var currency = await ResolveCurrencyAsync(currencyCode?.Trim(), ct);
        if (currency == null)
        {
            return ChatToolResult.Failure(
                $"Currency '{currencyCode ?? "CZK"}' not found or not active. " +
                "Please check available currencies in Settings.");
        }

        // ── Step 5: Get default VAT rate ─────────────────────────────────
        // We use the default standard rate (typically 21% in CZ). For a VAT payer a missing
        // default must be an error, not a silent 0% (issue #283): the invoice would otherwise
        // save with no VAT and nobody would notice until the VAT return.
        // A non-VAT payer is the opposite case — it has no rate to configure at all
        // (TenantReadinessService deliberately does not ask a non-payer for one) and 0% is the
        // correct value. InvoiceService.CreateInvoiceAsync draws exactly the same line: it demands
        // VatRateId only when the issuer is a VAT payer. Rejecting a non-payer here would block
        // from chat a document the UI creates without complaint.

        var defaultVatRate = await _vatRateService.GetDefaultStandardRateAsync(ct);
        if (issuer.IsVatPayer && defaultVatRate is null)
        {
            _logger.LogWarning(
                "CreateInvoiceTool: VAT-paying issuer {IssuerId} has no default standard VAT rate configured",
                issuer.Id);
            return ChatToolResult.Failure(
                "No default VAT rate is configured. Please set up a standard VAT rate in Settings before creating invoices.");
        }

        // ── Step 6: Parse items ──────────────────────────────────────────

        var parsedItems = await ChatInvoiceItems.ParseAsync(_reverseChargeCodeService, itemsJson, defaultVatRate?.Id, defaultVatRate?.Rate ?? 0m, ct);
        if (parsedItems.Error != null)
            return parsedItems.Error;

        // ── Step 7: Create the invoice ───────────────────────────────────

        try
        {
            var createDto = new CreateInvoiceDto
            {
                DocumentType = EDocumentType.Invoice,
                ClientId = client.Id,
                IssuerId = issuer.Id,
                CurrencyId = currency.Id,
                IssueDate = DateTime.UtcNow,
                PaymentMethod = EPaymentMethod.BankTransfer,
                Notes = notes?.Trim(),
                BankAccountId = bankAccountId,
                InvoiceItem = parsedItems.Items!
            };

            var createdInvoice = await _invoiceService.CreateInvoiceAsync(createDto, ct);

            _logger.LogInformation(
                "CreateInvoiceTool: invoice created successfully — ID={InvoiceId}, DocumentNumber={DocNumber}",
                createdInvoice.Id, createdInvoice.DocumentNumber);

            // ── Step 8: Return success with navigate action ──────────────

            // Build a summary of items for the AI response.
            var itemSummary = string.Join("\n",
                createdInvoice.InvoiceItem.Select(i =>
                    $"  - {i.Description}: {i.Quantity} × {i.UnitPrice:N2} {currency.Code}" +
                    $" = {i.TotalWithVat:N2} {currency.Code}"));

            return ChatToolResult.SuccessWithAction(
                $"Invoice created successfully:\n" +
                $"- Document number: {createdInvoice.DocumentNumber}\n" +
                $"- Client: {client.CompanyName}\n" +
                $"- Issue date: {createdInvoice.IssueDate:dd.MM.yyyy}\n" +
                $"- Due date: {createdInvoice.DueDate:dd.MM.yyyy}\n" +
                $"- Currency: {currency.Code}\n" +
                $"- Items:\n{itemSummary}\n" +
                $"- Total (incl. VAT): {createdInvoice.TotalWithVat:N2} {currency.Code}\n" +
                $"- Invoice ID: {createdInvoice.Id}",
                ChatUiAction.Navigate($"/invoices/{createdInvoice.Id}"));
        }
        catch (InvalidOperationException ex)
        {
            // Business rule violations (e.g., missing number sequence).
            _logger.LogWarning(ex, "CreateInvoiceTool: business error creating invoice");
            return ChatToolResult.Failure($"Could not create invoice: {ex.Message}");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "CreateInvoiceTool: unexpected error creating invoice");
            return ChatToolResult.Failure("An unexpected error occurred while creating the invoice.");
        }
    }

    // ─── Private Helpers ─────────────────────────────────────────────────

    /// <summary>
    /// Searches for a client by name. Returns single match or error.
    /// Reuses the same pattern as NavigateTool.ResolveClientAsync.
    /// </summary>
    private async Task<ClientResolveResult> ResolveClientAsync(
        string clientName, CancellationToken ct)
    {
        var filter = new ClientFilterDto
        {
            Search = clientName,
            PageSize = 5,
            IsIssuer = false // Only search customers, not the issuer.
        };

        var result = await _clientService.GetClientsPagedAsync(filter, ct);

        if (result.TotalCount == 0)
        {
            _logger.LogInformation("CreateInvoiceTool: no client found for '{ClientName}'", clientName);
            return new ClientResolveResult
            {
                Error = ChatToolResult.Failure(
                    $"No client found matching '{clientName}'. " +
                    "Please check the client name or create the client first.")
            };
        }

        if (result.TotalCount == 1)
        {
            var client = result.Items[0];
            _logger.LogInformation(
                "CreateInvoiceTool: resolved '{ClientName}' to client ID {ClientId} ({CompanyName})",
                clientName, client.Id, client.CompanyName);
            return new ClientResolveResult { Client = client };
        }

        // Multiple matches — user needs to clarify.
        _logger.LogInformation("CreateInvoiceTool: '{ClientName}' matched {Count} clients",
            clientName, result.TotalCount);

        var matchList = string.Join("\n",
            result.Items.Select(c =>
                $"  - {c.CompanyName} (ID: {c.Id}" +
                (string.IsNullOrEmpty(c.RegistrationNumber) ? ")" : $", IČO: {c.RegistrationNumber})")));

        return new ClientResolveResult
        {
            Error = ChatToolResult.Success(
                $"Found {result.TotalCount} clients matching '{clientName}':\n{matchList}\n\n" +
                "Please specify which client you mean (use a more specific name).")
        };
    }

    /// <summary>
    /// Resolves currency by code (e.g., "CZK", "EUR"). Defaults to CZK if not specified.
    /// Returns null if the currency is not found or inactive.
    /// </summary>
    private async Task<CurrencyDto?> ResolveCurrencyAsync(
        string? currencyCode, CancellationToken ct)
    {
        var code = string.IsNullOrWhiteSpace(currencyCode) ? "CZK" : currencyCode.ToUpperInvariant();
        return await _currencyService.GetCurrencyByCodeAsync(code, ct);
    }

    // ─── Internal result records ─────────────────────────────────────────

    private record ClientResolveResult
    {
        public ClientDto? Client { get; init; }
        public ChatToolResult? Error { get; init; }
    }
}

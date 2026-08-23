using System.Text.Json;
using System.Text.Json.Serialization;
using Fakvio.Application.Service;
using Fakvio.Contracts.Dto.Chat;
using Fakvio.Contracts.Dto.Client;
using Fakvio.Contracts.Dto.ReceivedInvoice;
using Microsoft.Extensions.Logging;

namespace Fakvio.Infrastructure.Service.ChatTools;

/// <summary>
/// Chat tool that records a received (incoming) invoice from data the user dictates in the
/// conversation: "zaeviduj přijatou fakturu od Alzy, 2 tonery po 1500".
///
/// How it differs from <see cref="ImportInvoiceTool"/> — the two are easy to confuse, and the
/// tool descriptions the model reads say the same thing:
///
/// | | <c>create_received_invoice</c> | <c>import_invoice</c> |
/// |---|---|---|
/// | Input | fields the user dictates | the text of a real document |
/// | Kind of invoice | always received | decided from the IČO on the document |
/// | Supplier | by name, must be one existing client | by IČO, name as fallback |
/// | Dates | optional, the service fills in what is missing | copied from the document, never invented |
///
/// So: a document in hand goes through import, a spoken expense through this tool.
///
/// Junior note: this tool writes, but it is not behind the confirm gate — creating a record is
/// additive and reversible (it can be deleted), and the existing <c>create_invoice</c> and
/// <c>import_invoice</c> are not gated either. Only the destructive delete is (DEVGUIDE §4.7).
/// </summary>
public class CreateReceivedInvoiceTool : IChatTool
{
    private readonly IReceivedInvoiceService _receivedInvoiceService;
    private readonly IClientService _clientService;
    private readonly ICurrencyService _currencyService;
    private readonly IVatRateService _vatRateService;
    private readonly ILogger<CreateReceivedInvoiceTool> _logger;

    public CreateReceivedInvoiceTool(
        IReceivedInvoiceService receivedInvoiceService,
        IClientService clientService,
        ICurrencyService currencyService,
        IVatRateService vatRateService,
        ILogger<CreateReceivedInvoiceTool> logger)
    {
        _receivedInvoiceService = receivedInvoiceService;
        _clientService = clientService;
        _currencyService = currencyService;
        _vatRateService = vatRateService;
        _logger = logger;
    }

    public string ToolName => "create_received_invoice";

    public string Description =>
        "Record a received (incoming/expense) invoice from data the user dictates: supplier, " +
        "items, dates. Use this when the user describes the expense in the conversation — " +
        "when they paste or upload the text of a real document, use 'import_invoice' instead, " +
        "which reads issued/received from the IČO and keeps the document's own dates.";

    private static readonly ChatToolParameter[] Schema =
    [
        new()
        {
            Name = "supplier_name",
            Type = ChatToolParameterType.String,
            Description = "Name of the supplier who issued the invoice. Must match one existing client.",
            IsRequired = true
        },
        new()
        {
            Name = "items",
            Type = ChatToolParameterType.ObjectArray,
            Description = "Line items. Each item has \"description\" (string, required), " +
                          "\"quantity\" (number, default 1), \"unit_price\" (number, required, excluding VAT) " +
                          "and optional \"vat_rate\" (number, percent). " +
                          "Example: [{\"description\": \"Toner\", \"quantity\": 2, \"unit_price\": 1500}]",
            IsRequired = true
        },
        new()
        {
            Name = "document_number",
            Type = ChatToolParameterType.String,
            Description = "Supplier's document number as printed on the invoice"
        },
        new()
        {
            Name = "issue_date",
            Type = ChatToolParameterType.String,
            Description = "Date the supplier issued the invoice, YYYY-MM-DD"
        },
        new()
        {
            Name = "due_date",
            Type = ChatToolParameterType.String,
            Description = "Payment due date, YYYY-MM-DD"
        },
        new()
        {
            Name = "taxable_supply_date",
            Type = ChatToolParameterType.String,
            Description = "Date of taxable supply (DUZP), YYYY-MM-DD — decides the VAT period"
        },
        new()
        {
            Name = "variable_symbol",
            Type = ChatToolParameterType.String,
            Description = "Variable symbol for the payment"
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
            Description = "Optional notes"
        }
    ];

    public IReadOnlyList<ChatToolParameter> Parameters => Schema;

    /// <summary>
    /// Resolves supplier, currency, VAT rate and items, then hands a complete DTO to the service.
    /// Everything that can be wrong is checked BEFORE the write, so a rejected call never leaves
    /// a half-recorded expense behind.
    /// </summary>
    public async Task<ChatToolResult> ExecuteAsync(
        Dictionary<string, string> parameters,
        CancellationToken ct = default)
    {
        // Required parameters are guaranteed present (and 'items' is guaranteed to be a JSON
        // array) by the executor's central validation — see IChatTool.ExecuteAsync.
        var supplierName = parameters["supplier_name"].Trim();
        var itemsJson = parameters["items"];

        _logger.LogInformation(
            "CreateReceivedInvoiceTool executing: supplier_name={Supplier}, items={Items}",
            supplierName, itemsJson);

        // ── Dates: read them first, they are the cheapest thing to reject ──
        if (!ChatToolDates.TryParseOptional(parameters, "issue_date", out var issueDate, out var dateError) ||
            !ChatToolDates.TryParseOptional(parameters, "due_date", out var dueDate, out dateError) ||
            !ChatToolDates.TryParseOptional(parameters, "taxable_supply_date", out var taxableSupplyDate, out dateError))
        {
            // A date the tool cannot read must never be dropped silently: the expense would land
            // in the wrong VAT period and nothing in the answer would say so.
            return ChatToolResult.Failure(dateError!);
        }

        // ── Supplier ───────────────────────────────────────────────────────
        var supplier = await ResolveSupplierAsync(supplierName, ct);
        if (supplier.Error is not null)
            return supplier.Error;

        // ── Currency ───────────────────────────────────────────────────────
        var currencyCode = GetOptional(parameters, "currency")?.ToUpperInvariant() ?? DefaultCurrencyCode;
        var currency = await _currencyService.GetCurrencyByCodeAsync(currencyCode, ct);
        if (currency is null)
        {
            return ChatToolResult.Failure(
                $"Currency '{currencyCode}' not found or not active. Check the available currencies in Settings.");
        }

        // ── Items ──────────────────────────────────────────────────────────
        var defaultVatRate = await _vatRateService.GetDefaultStandardRateAsync(ct);
        var items = ParseItems(itemsJson, defaultVatRate);
        if (items.Error is not null)
            return items.Error;

        // ── Write ──────────────────────────────────────────────────────────
        var dto = new CreateReceivedInvoiceDto
        {
            SupplierId = supplier.Client!.Id,
            CurrencyId = currency.Id,
            DocumentNumber = GetOptional(parameters, "document_number"),
            IssueDate = issueDate,
            DueDate = dueDate,
            // Left to the service when the user did not say: it falls back to the issue date,
            // which is the right default for DUZP far more often than "today" would be.
            TaxableSupplyDate = taxableSupplyDate,
            VariableSymbol = GetOptional(parameters, "variable_symbol"),
            Notes = GetOptional(parameters, "notes"),
            Items = items.Items!
        };

        try
        {
            var created = await _receivedInvoiceService.CreateAsync(dto, ct);

            _logger.LogInformation(
                "CreateReceivedInvoiceTool: created received invoice {Id} for supplier {SupplierId}",
                created.Id, dto.SupplierId);

            return ChatToolResult.SuccessWithAction(
                $"Received invoice recorded:\n{ReceivedInvoiceLookup.Describe(created)}",
                ChatUiAction.Navigate($"/received-invoices/{created.Id}"));
        }
        catch (InvalidOperationException ex)
        {
            _logger.LogInformation("CreateReceivedInvoiceTool: refused by business rule — {Reason}", ex.Message);
            return ChatToolResult.Failure(ex.Message);
        }
    }

    // ─── Private helpers ─────────────────────────────────────────────────

    /// <summary>Used when the user does not name a currency — the overwhelmingly common case.</summary>
    private const string DefaultCurrencyCode = "CZK";

    /// <summary>How many candidate suppliers to list back when the name is ambiguous.</summary>
    private const int MaxSupplierCandidates = 5;

    private static string? GetOptional(Dictionary<string, string> parameters, string key)
        => parameters.TryGetValue(key, out var value) && !string.IsNullOrWhiteSpace(value)
            ? value.Trim()
            : null;

    /// <summary>
    /// Finds the one client the supplier name refers to. Zero or several matches are both
    /// failures: guessing which company an expense belongs to is not the tool's decision.
    /// Issuers are excluded — the user's own company cannot invoice itself.
    /// </summary>
    private async Task<SupplierResolution> ResolveSupplierAsync(string supplierName, CancellationToken ct)
    {
        var matches = await _clientService.GetClientsPagedAsync(
            new ClientFilterDto
            {
                Search = supplierName,
                PageSize = MaxSupplierCandidates,
                IsIssuer = false
            },
            ct);

        if (matches.TotalCount == 0)
        {
            return SupplierResolution.Failed(
                $"No client found matching '{supplierName}'. Create the supplier first (create_client with their IČO).");
        }

        if (matches.TotalCount == 1)
            return new SupplierResolution { Client = matches.Items[0] };

        var candidates = string.Join("\n", matches.Items.Select(client =>
            $"  - {client.CompanyName} (ID: {client.Id}" +
            (string.IsNullOrEmpty(client.RegistrationNumber) ? ")" : $", IČO: {client.RegistrationNumber})")));

        return SupplierResolution.Failed(
            $"'{supplierName}' matches {matches.TotalCount} clients:\n{candidates}\nSay which supplier you mean.");
    }

    /// <summary>
    /// Turns the model's items array into line item DTOs.
    ///
    /// Junior note on the VAT rate: the default rate is passed on WITH its database id, but a rate
    /// the user dictated is passed on as a percentage only. That is not an oversight —
    /// <c>ReceivedInvoiceService</c> re-reads the percentage from the VatRate row whenever an id
    /// is present, so keeping the default id next to a 12% rate would silently bill it at 21%.
    /// </summary>
    private static ItemParseResult ParseItems(string itemsJson, Contracts.Dto.VatRate.VatRateDto? defaultVatRate)
    {
        List<RawItem>? rawItems;
        try
        {
            rawItems = JsonSerializer.Deserialize<List<RawItem>>(itemsJson, RawItemOptions);
        }
        catch (JsonException)
        {
            return ItemParseResult.Failed(
                "Could not read items. Expected format: " +
                """[{"description": "Toner", "quantity": 2, "unit_price": 1500}]""");
        }

        if (rawItems is null || rawItems.Count == 0)
            return ItemParseResult.Failed("The invoice needs at least one item.");

        var items = new List<CreateReceivedInvoiceItemDto>(rawItems.Count);

        for (var index = 0; index < rawItems.Count; index++)
        {
            var raw = rawItems[index];
            var position = index + 1;

            if (string.IsNullOrWhiteSpace(raw.Description))
                return ItemParseResult.Failed($"Item #{position} is missing 'description'.");

            if (raw.UnitPrice is not > 0)
                return ItemParseResult.Failed(
                    $"Item #{position} ('{raw.Description}') is missing a positive 'unit_price' (excluding VAT).");

            var usesDefaultRate = raw.VatRate is null;

            items.Add(new CreateReceivedInvoiceItemDto
            {
                OrderIndex = index,
                Description = raw.Description.Trim(),
                Quantity = raw.Quantity is > 0 ? raw.Quantity.Value : 1m,
                Unit = string.IsNullOrWhiteSpace(raw.Unit) ? DefaultUnit : raw.Unit.Trim(),
                UnitPrice = raw.UnitPrice.Value,
                VatRateId = usesDefaultRate ? defaultVatRate?.Id : null,
                VatRatePercentage = usesDefaultRate ? defaultVatRate?.Rate ?? 0m : raw.VatRate!.Value
            });
        }

        return new ItemParseResult { Items = items };
    }

    /// <summary>Czech "kusy" — the unit the UI also defaults to.</summary>
    private const string DefaultUnit = "ks";

    /// <summary>
    /// Case-insensitive so camelCase from one provider and snake_case from another both land,
    /// and tolerant of numbers sent as strings — models quote them surprisingly often.
    /// </summary>
    private static readonly JsonSerializerOptions RawItemOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        NumberHandling = JsonNumberHandling.AllowReadingFromString
    };

    /// <summary>One item exactly as the model sends it, before any defaulting.</summary>
    private sealed record RawItem
    {
        public string? Description { get; init; }

        public decimal? Quantity { get; init; }

        public string? Unit { get; init; }

        [JsonPropertyName("unit_price")]
        public decimal? UnitPrice { get; init; }

        [JsonPropertyName("vat_rate")]
        public decimal? VatRate { get; init; }
    }

    private sealed record SupplierResolution
    {
        public ClientDto? Client { get; init; }

        public ChatToolResult? Error { get; init; }

        public static SupplierResolution Failed(string message)
            => new() { Error = ChatToolResult.Failure(message) };
    }

    private sealed record ItemParseResult
    {
        public List<CreateReceivedInvoiceItemDto>? Items { get; init; }

        public ChatToolResult? Error { get; init; }

        public static ItemParseResult Failed(string message)
            => new() { Error = ChatToolResult.Failure(message) };
    }
}

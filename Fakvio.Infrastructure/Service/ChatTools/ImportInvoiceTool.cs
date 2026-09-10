using System.Text.Json;
using Fakvio.Application.Service;
using Fakvio.Contracts.Dto.Chat;
using Fakvio.Contracts.Dto.Invoice;
using Fakvio.Contracts.Dto.ReceivedInvoice;
using Fakvio.Domain.Enums;
using Fakvio.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Fakvio.Infrastructure.Service.ChatTools;

/// <summary>
/// Chat tool for importing invoices from user-provided data (PDF text, pasted content, prompt).
///
/// The AI extracts structured data from the user's message and calls this tool.
/// This tool then automatically:
///   1. Determines issued vs received by matching IČO against the company and client list
///   2. Finds the client/supplier in the database by IČO or name
///   3. Creates the invoice with ALL original dates preserved exactly
///   4. Returns the created invoice with a navigate action
///
/// The AI does NOT need to ask the user whether it's issued or received — this tool
/// decides automatically based on the issuer/recipient IČO matching the user's company.
///
/// Parameters from AI (all extracted from the invoice text):
///   - issuer_ico (string): IČO of the invoice issuer (dodavatel)
///   - issuer_name (string): Name of the invoice issuer
///   - recipient_ico (string): IČO of the invoice recipient (odběratel)
///   - recipient_name (string): Name of the invoice recipient
///   - document_number (string): Invoice number exactly as printed
///   - issue_date (string): YYYY-MM-DD — date of issue
///   - due_date (string): YYYY-MM-DD — payment due date
///   - taxable_supply_date (string): YYYY-MM-DD — DUZP
///   - variable_symbol (string): Variable symbol for payment
///   - bank_account (string): Bank account number
///   - iban (string): IBAN
///   - swift (string): SWIFT/BIC
///   - currency (string): ISO 4217 code (CZK, EUR, etc.)
///   - items (string): JSON array of line items
///   - notes (string): Optional notes
/// </summary>
public class ImportInvoiceTool : IChatTool
{
    private readonly IInvoiceService _invoiceService;
    private readonly IReceivedInvoiceService _receivedInvoiceService;
    private readonly IClientService _clientService;
    private readonly ICurrencyService _currencyService;
    private readonly IVatRateService _vatRateService;
    private readonly TenantDbContext _tenantContext;
    private readonly ILogger<ImportInvoiceTool> _logger;

    public ImportInvoiceTool(
        IInvoiceService invoiceService,
        IReceivedInvoiceService receivedInvoiceService,
        IClientService clientService,
        ICurrencyService currencyService,
        IVatRateService vatRateService,
        TenantDbContext tenantContext,
        ILogger<ImportInvoiceTool> logger)
    {
        _invoiceService = invoiceService;
        _receivedInvoiceService = receivedInvoiceService;
        _clientService = clientService;
        _currencyService = currencyService;
        _vatRateService = vatRateService;
        _tenantContext = tenantContext;
        _logger = logger;
    }

    public string ToolName => "import_invoice";

    public string Description =>
        "Imports an invoice from extracted data. Automatically determines if it's an issued " +
        "(vydaná) or received (přijatá) invoice by matching IČO against the company database. " +
        "Finds the client/supplier automatically. Preserves all dates exactly as extracted.";

    /// <summary>
    /// Parameter schema — static because it never changes per instance.
    /// Dates are strings on purpose: JSON Schema has no date type that models honour reliably,
    /// and the description pins the expected YYYY-MM-DD format.
    /// </summary>
    private static readonly ChatToolParameter[] Schema =
    [
        new()
        {
            Name = "issuer_ico",
            Type = ChatToolParameterType.String,
            Description = "IČO of the invoice issuer (dodavatel/vystavitel)"
        },
        new()
        {
            Name = "issuer_name",
            Type = ChatToolParameterType.String,
            Description = "Company name of the invoice issuer"
        },
        new()
        {
            Name = "recipient_ico",
            Type = ChatToolParameterType.String,
            Description = "IČO of the invoice recipient (odběratel/příjemce)"
        },
        new()
        {
            Name = "recipient_name",
            Type = ChatToolParameterType.String,
            Description = "Company name of the invoice recipient"
        },
        new()
        {
            Name = "document_number",
            Type = ChatToolParameterType.String,
            Description = "Invoice number EXACTLY as printed on the document",
            IsRequired = true
        },
        new()
        {
            Name = "issue_date",
            Type = ChatToolParameterType.String,
            Description = "Date of issue in YYYY-MM-DD format, EXACTLY from the invoice"
        },
        new()
        {
            Name = "due_date",
            Type = ChatToolParameterType.String,
            Description = "Payment due date in YYYY-MM-DD format, EXACTLY from the invoice"
        },
        new()
        {
            Name = "taxable_supply_date",
            Type = ChatToolParameterType.String,
            Description = "DUZP (taxable supply date) in YYYY-MM-DD format, EXACTLY from the invoice"
        },
        new()
        {
            Name = "variable_symbol",
            Type = ChatToolParameterType.String,
            Description = "Variable symbol (variabilní symbol) used for payment"
        },
        new()
        {
            Name = "bank_account",
            Type = ChatToolParameterType.String,
            Description = "Bank account number"
        },
        new()
        {
            Name = "iban",
            Type = ChatToolParameterType.String,
            Description = "IBAN"
        },
        new()
        {
            Name = "swift",
            Type = ChatToolParameterType.String,
            Description = "SWIFT/BIC code"
        },
        new()
        {
            Name = "currency",
            Type = ChatToolParameterType.String,
            Description = "ISO 4217 currency code (CZK, EUR, …)"
        },
        new()
        {
            Name = "items",
            Type = ChatToolParameterType.ObjectArray,
            Description = "Invoice line items. Each item has \"description\" (string), " +
                          "\"quantity\" (number), \"unit_price\" (number) and \"vat_rate\" (number, percent). " +
                          "Example: [{\"description\": \"Consulting\", \"quantity\": 1, " +
                          "\"unit_price\": 100, \"vat_rate\": 21}]",
            IsRequired = true
        },
        new()
        {
            Name = "notes",
            Type = ChatToolParameterType.String,
            Description = "Optional notes from the invoice"
        }
    ];

    public IReadOnlyList<ChatToolParameter> Parameters => Schema;

    public async Task<ChatToolResult> ExecuteAsync(
        Dictionary<string, string> parameters, CancellationToken ct = default)
    {
        try
        {
            // ── 1. Read parameters ──────────────────────────────────────────────
            var issuerIco = GetParam(parameters, "issuer_ico");
            var issuerName = GetParam(parameters, "issuer_name");
            var recipientIco = GetParam(parameters, "recipient_ico");
            var recipientName = GetParam(parameters, "recipient_name");
            var documentNumber = GetParam(parameters, "document_number");

            if (string.IsNullOrWhiteSpace(issuerIco) && string.IsNullOrWhiteSpace(issuerName)
                && string.IsNullOrWhiteSpace(recipientIco) && string.IsNullOrWhiteSpace(recipientName))
            {
                return ChatToolResult.Failure(
                    "Cannot import: no issuer or recipient data provided. " +
                    "Extract at least the IČO or company name from the invoice.");
            }

            // ── 2. Determine issued vs received ────────────────────────────────
            // Match IČO against the user's company (issuer in master DB).
            var myCompanyIco = await GetMyCompanyIcoAsync(ct);

            bool isIssuedByUs;
            if (!string.IsNullOrWhiteSpace(myCompanyIco))
            {
                if (issuerIco == myCompanyIco)
                    isIssuedByUs = true;
                else if (recipientIco == myCompanyIco)
                    isIssuedByUs = false;
                else
                {
                    // Neither IČO matches — try name matching as fallback
                    var myCompanyName = await GetMyCompanyNameAsync(ct);
                    if (!string.IsNullOrWhiteSpace(issuerName) && !string.IsNullOrWhiteSpace(myCompanyName)
                        && issuerName.Contains(myCompanyName, StringComparison.OrdinalIgnoreCase))
                        isIssuedByUs = true;
                    else if (!string.IsNullOrWhiteSpace(recipientName) && !string.IsNullOrWhiteSpace(myCompanyName)
                             && recipientName.Contains(myCompanyName, StringComparison.OrdinalIgnoreCase))
                        isIssuedByUs = false;
                    else
                        return ChatToolResult.Failure(
                            $"Cannot determine if this is an issued or received invoice. " +
                            $"Issuer IČO '{issuerIco}' and recipient IČO '{recipientIco}' " +
                            $"don't match your company IČO '{myCompanyIco}'.");
                }
            }
            else
            {
                return ChatToolResult.Failure("Your company is not configured. Go to My Company settings first.");
            }

            _logger.LogInformation("Import invoice: {Type}, doc={DocNum}, issuer={Issuer}, recipient={Recipient}",
                isIssuedByUs ? "ISSUED" : "RECEIVED", documentNumber, issuerName, recipientName);

            // ── 3. Find the counterparty (client/supplier) in DB ────────────────
            var counterpartyIco = isIssuedByUs ? recipientIco : issuerIco;
            var counterpartyName = isIssuedByUs ? recipientName : issuerName;

            var client = await FindClientAsync(counterpartyIco, counterpartyName, ct);
            if (client == null)
            {
                return ChatToolResult.Failure(
                    $"Client/supplier '{counterpartyName}' (IČO: {counterpartyIco}) not found in database. " +
                    $"Create the client first using the create_client tool with IČO {counterpartyIco}.");
            }

            // ── 4. Resolve currency ─────────────────────────────────────────────
            var currencyCode = GetParam(parameters, "currency") ?? "CZK";
            var currencies = await _currencyService.GetActiveCurrenciesAsync(ct);
            var currency = currencies.FirstOrDefault(c =>
                c.Code.Equals(currencyCode, StringComparison.OrdinalIgnoreCase) && c.IsActive);
            if (currency == null)
                return ChatToolResult.Failure($"Currency '{currencyCode}' not found.");

            // ── 5. Parse dates ──────────────────────────────────────────────────
            // Issue #301: this used to have its own TryParseExact copy that silently dropped an
            // unreadable date to null, so the invoice got created with a default date instead of
            // the one the user dictated. Routed through the shared ChatToolDates helper (like
            // every other chat tool) so a present-but-unreadable date fails loudly instead —
            // the model reads the error and retries with a readable date, same as ListInvoicesTool.
            if (!ChatToolDates.TryParseOptional(parameters, "issue_date", out var issueDate, out var dateError) ||
                !ChatToolDates.TryParseOptional(parameters, "due_date", out var dueDate, out dateError) ||
                !ChatToolDates.TryParseOptional(parameters, "taxable_supply_date", out var taxableSupplyDate, out dateError))
            {
                return ChatToolResult.Failure(dateError!);
            }

            // ── 6. Parse items ──────────────────────────────────────────────────
            var itemsJson = GetParam(parameters, "items");

            // ── 7. Create the invoice ───────────────────────────────────────────
            if (isIssuedByUs)
            {
                return await CreateIssuedInvoiceAsync(
                    client.Id, currency.Id, documentNumber,
                    issueDate, dueDate, taxableSupplyDate,
                    parameters, itemsJson, ct);
            }
            else
            {
                return await CreateReceivedInvoiceAsync(
                    client.Id, currency.Id, documentNumber,
                    issueDate, dueDate, taxableSupplyDate,
                    parameters, itemsJson, ct);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "ImportInvoiceTool failed");
            return ChatToolResult.Failure($"Import failed: {ex.Message}");
        }
    }

    // ─── Issued Invoice Creation ────────────────────────────────────────────────

    private async Task<ChatToolResult> CreateIssuedInvoiceAsync(
        long clientId, long currencyId, string? documentNumber,
        DateTime? issueDate, DateTime? dueDate, DateTime? taxableSupplyDate,
        Dictionary<string, string> parameters, string? itemsJson,
        CancellationToken ct)
    {
        var issuer = await _clientService.GetIssuerAsync(ct);
        if (issuer == null)
            return ChatToolResult.Failure("No issuer (your company) configured.");

        var vatRates = await _vatRateService.GetAllVatRatesAsync(cancellationToken: ct);
        var activeVatRates = vatRates.Where(v => v.IsActive).ToList();
        var defaultVatRate = activeVatRates.FirstOrDefault(v => v.IsDefault);

        var items = ParseItems(itemsJson, activeVatRates, defaultVatRate);
        if (items.Count == 0)
            return ChatToolResult.Failure("No valid line items found. Provide at least one item.");

        // Check for duplicate document number (excluding deleted)
        if (!string.IsNullOrWhiteSpace(documentNumber))
        {
            var exists = await _tenantContext.Invoice
                .AsNoTracking()
                .AnyAsync(i => i.DocumentNumber == documentNumber
                            && i.Status != EInvoiceStatus.Deleted, ct);
            if (exists)
                return ChatToolResult.Failure(
                    $"Invoice with number '{documentNumber}' already exists.");
        }

        // Resolve payment method: AI-extracted → issuer's default → BankTransfer fallback
        var paymentMethodParam = GetParam(parameters, "payment_method");
        EPaymentMethod? paymentMethod = null;
        if (!string.IsNullOrEmpty(paymentMethodParam)
            && Enum.TryParse<EPaymentMethod>(paymentMethodParam, true, out var parsed))
            paymentMethod = parsed;
        paymentMethod ??= issuer.BillingSettings?.DefaultPaymentMethod ?? EPaymentMethod.BankTransfer;

        var dto = new CreateInvoiceDto
        {
            DocumentType = EDocumentType.Invoice,
            ClientId = clientId,
            IssuerId = issuer.Id,
            CurrencyId = currencyId,
            CustomDocumentNumber = documentNumber,
            IssueDate = issueDate,
            DueDate = dueDate,
            TaxableSupplyDate = taxableSupplyDate,
            PaymentMethod = paymentMethod,
            VariableSymbol = GetParam(parameters, "variable_symbol"),
            BankAccountNumber = GetParam(parameters, "bank_account"),
            IBAN = GetParam(parameters, "iban"),
            SWIFT = GetParam(parameters, "swift"),
            Notes = GetParam(parameters, "notes"),
            InvoiceItem = items
        };

        var invoice = await _invoiceService.CreateInvoiceAsync(dto, ct);
        _logger.LogInformation("Imported issued invoice {Id} ({DocNum})", invoice.Id, invoice.DocumentNumber);

        return ChatToolResult.SuccessWithAction(
            $"Vydaná faktura {invoice.DocumentNumber} vytvořena (ID: {invoice.Id}).",
            ChatUiAction.Navigate($"/invoices/{invoice.Id}"));
    }

    // ─── Received Invoice Creation ──────────────────────────────────────────────

    private async Task<ChatToolResult> CreateReceivedInvoiceAsync(
        long supplierId, long currencyId, string? documentNumber,
        DateTime? issueDate, DateTime? dueDate, DateTime? taxableSupplyDate,
        Dictionary<string, string> parameters, string? itemsJson,
        CancellationToken ct)
    {
        var defaultVatRate = 21m;
        var vatRates = await _vatRateService.GetAllVatRatesAsync(cancellationToken: ct);
        var activeDefault = vatRates.FirstOrDefault(v => v.IsDefault && v.IsActive);
        if (activeDefault != null) defaultVatRate = activeDefault.Rate;

        var items = ParseReceivedItems(itemsJson, defaultVatRate);
        if (items.Count == 0)
            return ChatToolResult.Failure("No valid line items found.");

        // Resolve payment method: AI-extracted → BankTransfer fallback
        var paymentMethodParam = GetParam(parameters, "payment_method");
        EPaymentMethod? paymentMethod = null;
        if (!string.IsNullOrEmpty(paymentMethodParam)
            && Enum.TryParse<EPaymentMethod>(paymentMethodParam, true, out var parsedPm))
            paymentMethod = parsedPm;
        paymentMethod ??= EPaymentMethod.BankTransfer;

        var dto = new CreateReceivedInvoiceDto
        {
            SupplierId = supplierId,
            CurrencyId = currencyId,
            DocumentNumber = documentNumber,
            IssueDate = issueDate,
            DueDate = dueDate,
            TaxableSupplyDate = taxableSupplyDate,
            ReceivedDate = DateTime.UtcNow,
            PaymentMethod = paymentMethod,
            VariableSymbol = GetParam(parameters, "variable_symbol"),
            BankAccountNumber = GetParam(parameters, "bank_account"),
            IBAN = GetParam(parameters, "iban"),
            SWIFT = GetParam(parameters, "swift"),
            Notes = GetParam(parameters, "notes"),
            Items = items
        };

        var invoice = await _receivedInvoiceService.CreateAsync(dto, ct);
        _logger.LogInformation("Imported received invoice {Id} ({DocNum})", invoice.Id, invoice.DocumentNumber);

        return ChatToolResult.SuccessWithAction(
            $"Přijatá faktura {invoice.DocumentNumber} vytvořena (ID: {invoice.Id}).",
            ChatUiAction.Navigate($"/received-invoices/{invoice.Id}"));
    }

    // ─── Helpers ────────────────────────────────────────────────────────────────

    /// <summary>
    /// Gets the user's company IČO from the tenant DB (issuer client).
    /// </summary>
    private async Task<string?> GetMyCompanyIcoAsync(CancellationToken ct)
    {
        return await _tenantContext.Client
            .AsNoTracking()
            .Where(c => c.IsIssuer)
            .Select(c => c.RegistrationNumber)
            .FirstOrDefaultAsync(ct);
    }

    private async Task<string?> GetMyCompanyNameAsync(CancellationToken ct)
    {
        return await _tenantContext.Client
            .AsNoTracking()
            .Where(c => c.IsIssuer)
            .Select(c => c.CompanyName)
            .FirstOrDefaultAsync(ct);
    }

    /// <summary>
    /// Finds a client in the tenant DB by IČO (preferred) or name (fallback).
    /// </summary>
    private async Task<Contracts.Dto.Client.ClientDto?> FindClientAsync(
        string? ico, string? name, CancellationToken ct)
    {
        var clients = await _clientService.GetAllClientsAsync(cancellationToken: ct);

        // Priority 1: exact IČO match
        if (!string.IsNullOrWhiteSpace(ico))
        {
            var byIco = clients.FirstOrDefault(c =>
                c.RegistrationNumber == ico && c.IsActive);
            if (byIco != null) return byIco;
        }

        // Priority 2: name contains match (case-insensitive)
        if (!string.IsNullOrWhiteSpace(name))
        {
            var byName = clients
                .Where(c => c.IsActive && c.CompanyName != null
                    && c.CompanyName.Contains(name, StringComparison.OrdinalIgnoreCase))
                .ToList();
            if (byName.Count == 1) return byName[0];
        }

        return null;
    }

    private static string? GetParam(Dictionary<string, string> p, string key)
    {
        return p.TryGetValue(key, out var val) && !string.IsNullOrWhiteSpace(val) ? val.Trim() : null;
    }

    /// <summary>
    /// Parses AI-provided item JSON and resolves VatRateId from the active VAT rates.
    /// VatRateId is required when the issuer is a VAT payer — without it, CreateInvoiceAsync throws.
    /// Matches the AI-extracted percentage (e.g., 21) to the closest active VatRate entity.
    /// </summary>
    private static List<CreateInvoiceItemDto> ParseItems(
        string? json,
        List<Contracts.Dto.VatRate.VatRateDto> activeVatRates,
        Contracts.Dto.VatRate.VatRateDto? defaultVatRate)
    {
        if (string.IsNullOrWhiteSpace(json)) return new();
        try
        {
            var options = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };
            var raw = JsonSerializer.Deserialize<List<RawItem>>(json, options) ?? new();
            return raw.Select(r =>
            {
                var pct = r.VatRate > 0 ? r.VatRate : (defaultVatRate?.Rate ?? 21m);
                // Find the VatRate entity that matches this percentage (exact or closest).
                var matchedRate = activeVatRates.FirstOrDefault(v => v.Rate == pct) ?? defaultVatRate;

                return new CreateInvoiceItemDto
                {
                    Description = r.Description ?? "Item",
                    Quantity = r.Quantity > 0 ? r.Quantity : 1,
                    UnitPrice = r.UnitPrice,
                    VatRateId = matchedRate?.Id,
                    VatRatePercentage = pct,
                    Unit = r.Unit ?? "ks"
                };
            }).ToList();
        }
        catch { return new(); }
    }

    private static List<CreateReceivedInvoiceItemDto> ParseReceivedItems(string? json, decimal defaultVatRate)
    {
        if (string.IsNullOrWhiteSpace(json)) return new();
        try
        {
            var options = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };
            var raw = JsonSerializer.Deserialize<List<RawItem>>(json, options) ?? new();
            return raw.Select(r => new CreateReceivedInvoiceItemDto
            {
                Description = r.Description ?? "Item",
                Quantity = r.Quantity > 0 ? r.Quantity : 1,
                UnitPrice = r.UnitPrice,
                VatRatePercentage = r.VatRate > 0 ? r.VatRate : defaultVatRate
            }).ToList();
        }
        catch { return new(); }
    }

    /// <summary>
    /// Raw deserialization target for AI-provided item JSON.
    /// Supports both snake_case and camelCase from different AI providers.
    /// </summary>
    private class RawItem
    {
        public string? Description { get; set; }
        public decimal Quantity { get; set; } = 1;
        public decimal UnitPrice { get; set; }
        public decimal Unit_Price { set => UnitPrice = value; }
        public decimal VatRate { get; set; }
        public decimal Vat_Rate { set => VatRate = value; }
        public string? Unit { get; set; }
    }
}

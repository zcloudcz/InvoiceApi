using System.Text.RegularExpressions;
using Fakvio.Application.Service;
using Fakvio.Contracts.Dto.Chat;
using Fakvio.Contracts.Dto.Invoice;
using Fakvio.Domain.Enums;
using Microsoft.Extensions.Logging;

namespace Fakvio.Infrastructure.Service.ChatTools;

/// <summary>
/// Chat tool that edits an existing DRAFT invoice (Completed ones need revert_invoice_to_draft first). Mirrors MCP
/// <c>update_invoice</c> (minus applyOss and the OSS regime: OSS invoices are edited in the app).
///
/// Partial update: only the parameters the model sends are changed, everything else stays.
/// <c>items</c> REPLACES all lines. Data-changing, so it goes through the confirm gate
/// (<see cref="IConfirmableChatTool"/>, DEVGUIDE §4.7): the first call only lists the changes.
/// </summary>
public class UpdateInvoiceTool : IConfirmableChatTool
{
    private readonly IInvoiceService _invoiceService;
    private readonly IClientService _clientService;
    private readonly ICurrencyService _currencyService;
    private readonly IVatRateService _vatRateService;
    private readonly IReverseChargeCodeService _reverseChargeCodeService;
    private readonly ILogger<UpdateInvoiceTool> _logger;

    public UpdateInvoiceTool(
        IInvoiceService invoiceService,
        IClientService clientService,
        ICurrencyService currencyService,
        IVatRateService vatRateService,
        IReverseChargeCodeService reverseChargeCodeService,
        ILogger<UpdateInvoiceTool> logger)
    {
        _invoiceService = invoiceService;
        _clientService = clientService;
        _currencyService = currencyService;
        _vatRateService = vatRateService;
        _reverseChargeCodeService = reverseChargeCodeService;
        _logger = logger;
    }

    public string ToolName => "update_invoice";

    public string Description =>
        "Edit an existing DRAFT invoice, credit note or proforma: dates, " +
        "payment symbols, payment method, bank account, currency, notes and/or line items. Send only the " +
        "fields that should change. 'items' REPLACES ALL lines, so call get_invoice first and send the " +
        "complete list. An issued (Completed) invoice is not changed: the result tells you to ask the user and use " +
        "revert_invoice_to_draft first. Paid and credit-noted documents cannot be edited; the client cannot be changed. " +
        "Identify the invoice by ID or document number.";

    private static readonly ChatToolParameter[] Schema =
    [
        ..InvoiceLookup.IdentitySchema,
        new() { Name = "issue_date", Type = ChatToolParameterType.String, Description = "New issue date (YYYY-MM-DD)" },
        new() { Name = "due_date", Type = ChatToolParameterType.String, Description = "New due date (YYYY-MM-DD)" },
        new() { Name = "taxable_supply_date", Type = ChatToolParameterType.String, Description = "New date of taxable supply / DUZP (YYYY-MM-DD)" },
        new() { Name = "variable_symbol", Type = ChatToolParameterType.String, Description = "New variable symbol (digits only, max 10)" },
        new() { Name = "constant_symbol", Type = ChatToolParameterType.String, Description = "New constant symbol (max 50 characters)" },
        new() { Name = "specific_symbol", Type = ChatToolParameterType.String, Description = "New specific symbol (max 50 characters)" },
        new()
        {
            Name = "payment_method",
            Type = ChatToolParameterType.String,
            Description = "New payment method: BankTransfer, Cash, CreditCard, PayPal or Other"
        },
        new()
        {
            Name = "bank_account_id",
            Type = ChatToolParameterType.Integer,
            Description = "One of the issuer's bank account ids (see get_my_company)"
        },
        new() { Name = "currency", Type = ChatToolParameterType.String, Description = "New ISO 4217 currency code (CZK, EUR, …)" },
        new() { Name = "notes", Type = ChatToolParameterType.String, Description = "New notes text (replaces the old one)" },
        new()
        {
            Name = "items",
            Type = ChatToolParameterType.ObjectArray,
            Description = "COMPLETE replacement list of line items (all existing lines are removed). Same shape as " +
                          "create_invoice: each item has \"description\" (required), \"quantity\" (default 1), " +
                          "\"unit_price\" (required), optional \"unit\", \"vat_regime\" and \"reverse_charge_code\". " +
                          "Example: [{\"description\": \"Web development\", \"quantity\": 10, \"unit_price\": 1500}]"
        }
    ];

    public IReadOnlyList<ChatToolParameter> Parameters => Schema;

    public async Task<ChatToolResult> BuildPreviewAsync(
        Dictionary<string, string> parameters,
        CancellationToken ct = default)
    {
        var plan = await PlanAsync(parameters, ct);
        if (plan.Error is not null)
            return ChatToolResult.Failure(plan.Error);

        return ChatToolResult.Success(
            $"Will update {InvoiceLookup.Describe(plan.Invoice!)}:\n- " + string.Join("\n- ", plan.Changes));
    }

    public async Task<ChatToolResult> ExecuteAsync(
        Dictionary<string, string> parameters,
        CancellationToken ct = default)
    {
        // Re-checked: the preview and this call are not paired (see IConfirmableChatTool).
        var plan = await PlanAsync(parameters, ct);
        if (plan.Error is not null)
            return ChatToolResult.Failure(plan.Error);

        _logger.LogInformation("UpdateInvoiceTool updating invoice {InvoiceId}", plan.Invoice!.Id);

        try
        {
            var updated = await _invoiceService.UpdateInvoiceAsync(plan.Invoice.Id, plan.Dto!, ct);
            if (updated is null)
                return ChatToolResult.Failure($"Issued invoice with ID {plan.Invoice.Id} not found.");

            return ChatToolResult.SuccessWithAction(
                $"Updated {InvoiceLookup.Describe(updated)}:\n- " + string.Join("\n- ", plan.Changes),
                ChatUiAction.Navigate($"/invoices/{updated.Id}"));
        }
        catch (InvalidOperationException ex)
        {
            // Business rule violations (e.g. status, VAT rate, bank account of another issuer).
            _logger.LogWarning(ex, "UpdateInvoiceTool: business error updating invoice");
            return ChatToolResult.Failure($"Could not update invoice: {ex.Message}");
        }
    }

    private record UpdatePlan(InvoiceDto? Invoice, UpdateInvoiceDto? Dto, List<string> Changes, string? Error);

    private static UpdatePlan Fail(string error) => new(null, null, [], error);

    /// <summary>
    /// Validates every parameter and builds the DTO plus a human-readable change list.
    /// Nothing is written. Used by both the preview and the execution so they cannot disagree.
    /// </summary>
    private async Task<UpdatePlan> PlanAsync(Dictionary<string, string> parameters, CancellationToken ct)
    {
        var (invoice, lookupError) = await InvoiceLookup.ResolveAsync(_invoiceService, parameters, ct);
        if (invoice is null)
            return Fail(lookupError!);

        // Issued documents are never edited silently: the user must first agree to switch them back to Draft.
        if (invoice.Status == EInvoiceStatus.Completed)
            return Fail(
                $"REQUIRES_REVERT_TO_DRAFT: {InvoiceLookup.Describe(invoice)} is already issued (Completed), so nothing was changed. " +
                "To edit it, it must first be switched back to Draft. Ask the user whether to do that and explain the consequences: " +
                "the issued document becomes a draft, it must be issued again with complete_invoice after editing, and re-sent " +
                "if it was already e-mailed. Only after the user agrees, call revert_invoice_to_draft and then update_invoice again.");

        if (invoice.Status != EInvoiceStatus.Draft)
            return Fail(
                $"{InvoiceLookup.Describe(invoice)} is {invoice.Status} — it cannot be edited. To correct it, issue a credit " +
                "note and a new invoice; if a payment was recorded by mistake, mark the invoice as unpaid in the app first.");

        var dto = new UpdateInvoiceDto();
        var changes = new List<string>();

        // ── Dates ──────────────────────────────────────────────────────
        foreach (var (key, label, apply) in new (string, string, Action<DateTime>)[]
        {
            ("issue_date", "issue date", d => dto.IssueDate = d),
            ("due_date", "due date", d => dto.DueDate = d),
            ("taxable_supply_date", "taxable supply date", d => dto.TaxableSupplyDate = d)
        })
        {
            if (!ChatToolDates.TryParseOptional(parameters, key, out var date, out var dateError))
                return Fail(dateError!);
            if (date is null)
                continue;
            apply(date.Value);
            changes.Add($"{label} → {ChatToolDates.Format(date)}");
        }

        // ── Symbols (the API DTO has attribute validation that a direct service call skips) ──
        var variableSymbol = Read(parameters, "variable_symbol");
        if (variableSymbol is not null)
        {
            if (!Regex.IsMatch(variableSymbol, @"^\d{1,10}$"))
                return Fail("variable_symbol must contain only digits (max 10).");
            dto.VariableSymbol = variableSymbol;
            changes.Add($"variable symbol → {variableSymbol}");
        }

        var constantSymbol = Read(parameters, "constant_symbol");
        if (constantSymbol is not null)
        {
            if (constantSymbol.Length > 50)
                return Fail("constant_symbol must be at most 50 characters.");
            dto.ConstantSymbol = constantSymbol;
            changes.Add($"constant symbol → {constantSymbol}");
        }

        var specificSymbol = Read(parameters, "specific_symbol");
        if (specificSymbol is not null)
        {
            if (specificSymbol.Length > 50)
                return Fail("specific_symbol must be at most 50 characters.");
            dto.SpecificSymbol = specificSymbol;
            changes.Add($"specific symbol → {specificSymbol}");
        }

        var notes = Read(parameters, "notes");
        if (notes is not null)
        {
            if (notes.Length > 5000)
                return Fail("notes must be at most 5000 characters.");
            dto.Notes = notes;
            changes.Add("notes replaced");
        }

        // ── Payment method / bank account / currency ───────────────────
        var paymentMethod = Read(parameters, "payment_method");
        if (paymentMethod is not null)
        {
            if (!Enum.TryParse<EPaymentMethod>(paymentMethod, ignoreCase: true, out var pm) || !Enum.IsDefined(pm))
                return Fail(
                    $"Unknown payment_method '{paymentMethod}'. Valid values: " +
                    string.Join(", ", Enum.GetNames<EPaymentMethod>()) + ".");
            dto.PaymentMethod = pm;
            changes.Add($"payment method → {pm}");
        }

        var bankAccountRaw = Read(parameters, "bank_account_id");
        if (bankAccountRaw is not null)
        {
            // Present but not a number: fail loudly instead of silently keeping the old account.
            if (!long.TryParse(bankAccountRaw, out var bankAccountId))
                return Fail("bank_account_id must be a number");
            dto.BankAccountId = bankAccountId;
            changes.Add($"bank account → ID {bankAccountId}");
        }

        var currencyCode = Read(parameters, "currency");
        if (currencyCode is not null)
        {
            var currency = await _currencyService.GetCurrencyByCodeAsync(currencyCode.ToUpperInvariant(), ct);
            if (currency is null)
                return Fail($"Currency '{currencyCode}' not found or not active.");
            dto.CurrencyId = currency.Id;
            changes.Add($"currency → {currency.Code}");
        }

        // ── Items (full replacement) ───────────────────────────────────
        var itemsJson = Read(parameters, "items");
        if (itemsJson is not null)
        {
            if (invoice.OssCountryCode is not null)
                return Fail("This invoice uses the EU OSS regime — edit its items in the app, not from chat.");

            var issuer = await _clientService.GetIssuerAsync(ct);
            if (issuer is null)
                return Fail("No issuer (your company) is configured. Please set up your company first in Settings.");

            // Same rule as create_invoice: a VAT payer without a default rate is an error, not 0 %.
            var defaultVatRate = await _vatRateService.GetDefaultStandardRateAsync(ct);
            if (issuer.IsVatPayer && defaultVatRate is null)
                return Fail("No default VAT rate is configured. Please set up a standard VAT rate in Settings.");

            var parsed = await ChatInvoiceItems.ParseAsync(
                _reverseChargeCodeService, itemsJson, defaultVatRate?.Id, defaultVatRate?.Rate ?? 0m, ct);
            if (parsed.Error is not null)
                return Fail(parsed.Error.ErrorMessage ?? parsed.Error.OutputText);

            dto.InvoiceItem = parsed.Items;
            changes.Add($"ALL line items replaced with {parsed.Items!.Count} new item(s)");
        }

        if (changes.Count == 0)
            return Fail("Nothing to update — send at least one field to change.");

        return new UpdatePlan(invoice, dto, changes, null);
    }

    /// <summary>Returns the trimmed parameter value, or null when missing or blank.</summary>
    private static string? Read(Dictionary<string, string> parameters, string key)
        => parameters.TryGetValue(key, out var raw) && !string.IsNullOrWhiteSpace(raw) ? raw.Trim() : null;
}

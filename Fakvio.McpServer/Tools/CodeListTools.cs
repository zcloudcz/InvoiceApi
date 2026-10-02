using System.ComponentModel;
using System.Text.Json;
using Fakvio.Contracts.Dto.Currency;
using Fakvio.McpServer.Client;
using ModelContextProtocol.Server;
using Fakvio.Contracts.Dto.ReverseChargeCode;

namespace Fakvio.McpServer.Tools;

/// <summary>
/// MCP tools that list small, mostly-static "code lists" (lookup tables) an AI client needs
/// to fill in other tools' parameters correctly — e.g. which currency codes are valid before
/// calling <c>create_invoice</c> with <c>currency: "EUR"</c>.
///
/// Junior note: keeping this in its own file (rather than adding to InvoiceTools/ClientTools)
/// mirrors the API side, where currencies/VAT rates/number sequences are their own controllers,
/// not folded into InvoiceController — a code list is a different kind of thing than a document.
/// </summary>
[McpServerToolType]
public static class CodeListTools
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        WriteIndented = true,
        Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter() }
    };

    /// <summary>
    /// Lists the currencies the tenant can invoice in. Narrowed to the fields a client needs to
    /// pick one (id, code, name, symbol) — the API's CurrencyDto also carries sort order and
    /// display format, which are UI concerns an AI client has no use for.
    /// </summary>
    [McpServerTool(Title = "List currencies", ReadOnly = true, Idempotent = true, OpenWorld = false), Description(
        "List active currencies. Use the code (e.g. 'EUR') when creating invoices — " +
        "create_invoice and create_received_invoice accept a currency code directly.")]
    public static async Task<string> ListCurrencies(
        IFakvioApiClient api,
        CancellationToken ct = default)
    {
        try
        {
            var currencies = await api.GetActiveCurrenciesAsync(ct);
            var narrowed = currencies.Select(c => new
            {
                c.Id,
                c.Code,
                c.Name,
                c.Symbol
            });

            return JsonSerializer.Serialize(narrowed, JsonOptions);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            return McpToolError.ToJson(ex);
        }
    }

    /// <summary>
    /// Official ČNB exchange rate for a currency on a date — what Czech VAT (§38 ZDPH) uses to convert a
    /// foreign-currency invoice to CZK. Issued/received invoices get the same rate automatically (DUZP).
    /// </summary>
    [McpServerTool(Title = "Get exchange rate", ReadOnly = true, Idempotent = true, OpenWorld = false), Description(
        "Get the official Czech National Bank (ČNB) exchange rate to CZK for a currency on a date: the last daily fixing " +
        "declared on or before that date (weekends/holidays get the previous business day). Returns the published rate " +
        "(for amount units, e.g. 100 for HUF/JPY), ratePerUnit (CZK for exactly one unit) and validFor (the fixing date). " +
        "Foreign-currency invoices store this rate (valid for the taxable supply date) when they are issued.")]
    public static async Task<string> GetExchangeRate(
        IFakvioApiClient api,
        [Description("ISO 4217 currency code other than CZK, e.g. \"EUR\"")] string currency,
        [Description("Date (yyyy-MM-dd). Omit for today.")] DateOnly? date = null,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(currency) || currency.Trim().Length != 3 ||
            currency.Trim().Equals("CZK", StringComparison.OrdinalIgnoreCase))
            return JsonSerializer.Serialize(new { error = "currency must be a 3-letter ISO code other than CZK." }, JsonOptions);

        try
        {
            var rate = await api.GetExchangeRateAsync(currency.Trim(), date, ct);
            return rate is null
                ? JsonSerializer.Serialize(new { error = $"No ČNB rate available for {currency.Trim().ToUpperInvariant()}." }, JsonOptions)
                : JsonSerializer.Serialize(rate, JsonOptions);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            return McpToolError.ToJson(ex);
        }
    }

    /// <summary>
    /// Lists reverse charge codes (kódy předmětu plnění PDP, §92a-92e ZDPH) — the AI client needs
    /// one of these codes (its "id" field, passed as reverseChargeCodeId) when creating an invoice item with
    /// vatRegime "ReverseCharge" via create_invoice.
    /// </summary>
    [McpServerTool(Title = "List reverse charge codes", ReadOnly = true, Idempotent = true, OpenWorld = false), Description(
        "List active reverse charge codes (kódy předmětu plnění PDP, §92a-92e ZDPH). " +
        "Pass the 'id' field as reverseChargeCodeId when creating an invoice item with vatRegime 'ReverseCharge' (the 'code' like '4' is only the human-readable label).")]
    public static async Task<string> ListReverseChargeCodes(
        IFakvioApiClient api,
        CancellationToken ct = default)
    {
        try
        {
            var codes = await api.GetActiveReverseChargeCodesAsync(ct);
            var narrowed = codes.Select(c => new
            {
                c.Id,
                c.Code,
                c.NameCs,
                c.NameEn,
                c.ParagraphRef
            });

            return JsonSerializer.Serialize(narrowed, JsonOptions);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            return McpToolError.ToJson(ex);
        }
    }

    /// <summary>
    /// Resolves a model-supplied currency code (or <see langword="null"/>, meaning "CZK") to the
    /// active <see cref="CurrencyDto"/> it identifies. Shared by <c>InvoiceTools.CreateInvoice</c>
    /// and <c>ReceivedInvoiceTools.CreateReceivedInvoice</c> (N2.4 / N2.5) — both need exactly the
    /// same lookup instead of the model providing an internal <c>currencyId</c> it cannot know.
    /// </summary>
    /// <returns>
    /// The resolved currency, or <see langword="null"/> with <paramref name="error"/> set to a
    /// message listing the active codes when the requested code does not match any of them.
    /// </returns>
    internal static async Task<(CurrencyDto? Currency, string? Error)> ResolveCurrencyAsync(
        IFakvioApiClient api, string? code, CancellationToken ct)
    {
        var currencies = await api.GetActiveCurrenciesAsync(ct);
        var wantedCode = code ?? "CZK";
        var match = currencies.FirstOrDefault(
            c => string.Equals(c.Code, wantedCode, StringComparison.OrdinalIgnoreCase));

        return match is not null
            ? (match, null)
            : (null, $"Unknown currency '{wantedCode}'. Active currencies: " +
                     string.Join(", ", currencies.Select(c => c.Code)) + ".");
    }
}

using System.ComponentModel;
using System.Text.Json;
using Fakvio.McpServer.Client;
using ModelContextProtocol.Server;

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
}

using System.ComponentModel;
using System.Text.Json;
using Fakvio.Contracts.Dto.NumberSequence;
using Fakvio.Domain.Enums;
using Fakvio.McpServer.Client;
using ModelContextProtocol.Server;

namespace Fakvio.McpServer.Tools;

/// <summary>
/// MCP tools for company settings that block invoicing when missing — number sequences and VAT
/// rates today (N3.1). Story N3: after the in-app AI assistant was hidden (#441), MCP is the
/// only AI channel, so it has to be able to fix what get_readiness reports as missing, not just
/// report it.
/// </summary>
[McpServerToolType]
public static class SettingsTools
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        WriteIndented = true,
        Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter() }
    };

    /// <summary>
    /// Lists the tenant's document number sequences, together with the available numbering
    /// formats — a model needs a format ID to call <see cref="CreateNumberSequence"/>, and this
    /// is the only tool that hands one out.
    /// </summary>
    [McpServerTool(Title = "List number sequences", ReadOnly = true, Idempotent = true, OpenWorld = false), Description(
        "List the company's document number sequences (číselné řady): name, document type, " +
        "prefix/suffix, current counter, numbering format and which one is the default. " +
        "Also lists the available numbering formats with the IDs needed by create_number_sequence. " +
        "Fixes NUMBER_SEQUENCE_MISSING from get_readiness.")]
    public static async Task<string> ListNumberSequences(
        IFakvioApiClient api,
        [Description("Filter by document type: 'Invoice', 'CreditNote', 'Proforma', 'TaxReceiptForAdvance'. Omit for all.")] string? documentType = null,
        [Description("Include deactivated sequences and formats (default false)")] bool includeInactive = false,
        CancellationToken ct = default)
    {
        EDocumentType? parsedType = null;
        if (!string.IsNullOrWhiteSpace(documentType))
        {
            if (!Enum.TryParse<EDocumentType>(documentType, ignoreCase: true, out var dt))
                return Error($"Unknown documentType '{documentType}'. Valid values: " +
                             string.Join(", ", Enum.GetNames<EDocumentType>()) + ".");
            parsedType = dt;
        }

        try
        {
            var sequences = await api.GetNumberSequencesAsync(parsedType, includeInactive, ct);
            var formats = await api.GetNumberSequenceFormatsAsync(includeInactive, ct);

            return JsonSerializer.Serialize(new { sequences, formats }, JsonOptions);
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
    /// Lists VAT rates valid at a given date — the same data <see cref="InvoiceTools.CreateInvoice"/>
    /// resolves <c>vatRatePercentage</c> against, exposed directly so the model (or the user) can
    /// see what percentages are currently valid.
    /// </summary>
    [McpServerTool(Title = "List VAT rates", ReadOnly = true, Idempotent = true, OpenWorld = false), Description(
        "List VAT (DPH) rates valid at a given date — percentage, name, whether it is reduced " +
        "or the default standard rate.")]
    public static async Task<string> ListVatRates(
        IFakvioApiClient api,
        [Description("Date to check validity, ISO 8601 (e.g. '2026-01-01'). Omit for today.")] string? date = null,
        CancellationToken ct = default)
    {
        DateTime? parsedDate = null;
        if (!string.IsNullOrWhiteSpace(date))
        {
            if (!DateTime.TryParse(date, out var d))
                return Error($"Invalid date '{date}'. Use ISO 8601 (e.g. '2026-01-01').");
            parsedDate = d;
        }

        try
        {
            var rates = await api.GetActiveVatRatesAsync(parsedDate, ct);
            return JsonSerializer.Serialize(rates, JsonOptions);
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

    private static string Error(string message) =>
        JsonSerializer.Serialize(new { error = message }, JsonOptions);
}

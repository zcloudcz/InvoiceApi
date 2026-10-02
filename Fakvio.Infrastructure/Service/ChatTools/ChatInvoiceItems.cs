using System.Globalization;
using System.Text.Json;
using Fakvio.Application.Service;
using Fakvio.Contracts.Dto.Invoice;
using Fakvio.Domain.Enums;

namespace Fakvio.Infrastructure.Service.ChatTools;

/// <summary>
/// Parses the "items" JSON array the model sends to the invoice chat tools (create_invoice,
/// update_invoice). Lives in one place so both tools accept exactly the same item shape.
/// </summary>
internal static class ChatInvoiceItems
{
    /// <summary>
    /// Parses the items JSON array from the AI response.
    /// Expected format: [{"description": "Mléko", "quantity": 1, "unit_price": 999}]
    ///
    /// Handles common AI formatting variations:
    /// - Missing quantity → defaults to 1
    /// - Missing description → error
    /// - Missing unit_price → error
    /// - "total_price" instead of "unit_price" → treated as total price for 1 unit
    /// - "vat_regime": "ReverseCharge" + "reverse_charge_code": "4" → PDP item (§92a-92e ZDPH);
    ///   the code string is resolved to a ReverseChargeCodeId via IReverseChargeCodeService,
    ///   same validation InvoiceService.ValidateReverseChargeCodes enforces server-side.
    /// </summary>
    internal static async Task<ItemParseResult> ParseAsync(
        IReverseChargeCodeService reverseChargeCodeService, string itemsJson, long? defaultVatRateId, decimal defaultVatPercentage, CancellationToken ct)
    {
        try
        {
            using var doc = JsonDocument.Parse(itemsJson);
            var root = doc.RootElement;

            if (root.ValueKind != JsonValueKind.Array || root.GetArrayLength() == 0)
            {
                return new ItemParseResult
                {
                    Error = ChatToolResult.Failure(
                        "Items must be a non-empty JSON array. " +
                        "Example: [{\"description\": \"Product\", \"quantity\": 1, \"unit_price\": 100}]")
                };
            }

            var items = new List<CreateInvoiceItemDto>();
            var orderIndex = 1;

            foreach (var element in root.EnumerateArray())
            {
                // Description is required.
                var description = GetJsonString(element, "description");
                if (string.IsNullOrWhiteSpace(description))
                {
                    return new ItemParseResult
                    {
                        Error = ChatToolResult.Failure(
                            $"Item #{orderIndex} is missing 'description'. Each item needs a description.")
                    };
                }

                // Get quantity — missing stays the documented default of 1, but a quantity the
                // model explicitly sent as zero or negative is rejected instead of silently
                // becoming 1 (issue #283): that would create an invoice for a quantity nobody asked for.
                var quantityRaw = GetJsonDecimal(element, "quantity");
                if (quantityRaw is <= 0)
                {
                    return new ItemParseResult
                    {
                        Error = ChatToolResult.Failure(
                            $"Item #{orderIndex} ('{description}') has invalid quantity " +
                            $"({quantityRaw.Value.ToString(CultureInfo.InvariantCulture)}). " +
                            "Quantity must be positive; omit it to default to 1.")
                    };
                }
                var quantity = quantityRaw ?? 1m;

                // Get price — try "unit_price" first, then "price", then "total_price".
                var unitPrice = GetJsonDecimal(element, "unit_price")
                    ?? GetJsonDecimal(element, "price")
                    ?? GetJsonDecimal(element, "total_price");

                if (unitPrice == null || unitPrice <= 0)
                {
                    return new ItemParseResult
                    {
                        Error = ChatToolResult.Failure(
                            $"Item #{orderIndex} ('{description}') is missing 'unit_price'. " +
                            "Each item needs a price.")
                    };
                }

                // Get optional unit — default to "ks" (pieces in Czech).
                var unit = GetJsonString(element, "unit") ?? "ks";

                // Optional reverse charge regime (§92a-92e ZDPH). Unrecognized "vat_regime" values
                // are rejected rather than silently falling back to Standard — the AI would otherwise
                // never learn it typed the regime name wrong.
                var vatRegime = EVatRegime.Standard;
                var vatRegimeRaw = GetJsonString(element, "vat_regime");
                if (!string.IsNullOrWhiteSpace(vatRegimeRaw) &&
                    (!Enum.TryParse(vatRegimeRaw, ignoreCase: true, out vatRegime) || !Enum.IsDefined(vatRegime)))
                {
                    return new ItemParseResult
                    {
                        Error = ChatToolResult.Failure(
                            $"Item #{orderIndex} ('{description}') has invalid 'vat_regime' " +
                            $"'{vatRegimeRaw}'. Use one of: Standard, ReverseCharge, Exempt, OutOfScope.")
                    };
                }

                long? reverseChargeCodeId = null;
                var reverseChargeCodeRaw = GetJsonString(element, "reverse_charge_code");
                if (vatRegime == EVatRegime.ReverseCharge)
                {
                    if (string.IsNullOrWhiteSpace(reverseChargeCodeRaw))
                    {
                        return new ItemParseResult
                        {
                            Error = ChatToolResult.Failure(
                                $"Item #{orderIndex} ('{description}') has vat_regime=ReverseCharge but no " +
                                "'reverse_charge_code'. A reverse charge code (kód předmětu plnění) is required.")
                        };
                    }

                    var reverseChargeCode = await reverseChargeCodeService.GetByCodeAsync(reverseChargeCodeRaw, ct);
                    if (reverseChargeCode == null)
                    {
                        return new ItemParseResult
                        {
                            Error = ChatToolResult.Failure(
                                $"Item #{orderIndex} ('{description}'): reverse charge code " +
                                $"'{reverseChargeCodeRaw}' was not found. Use list_reverse_charge_codes to see valid codes.")
                        };
                    }
                    reverseChargeCodeId = reverseChargeCode.Id;
                }
                else if (!string.IsNullOrWhiteSpace(reverseChargeCodeRaw))
                {
                    return new ItemParseResult
                    {
                        Error = ChatToolResult.Failure(
                            $"Item #{orderIndex} ('{description}') has 'reverse_charge_code' set but " +
                            $"vat_regime={vatRegime}. The code is only valid for vat_regime=ReverseCharge.")
                    };
                }

                items.Add(new CreateInvoiceItemDto
                {
                    OrderIndex = orderIndex,
                    Description = description,
                    Quantity = quantity,
                    Unit = unit,
                    UnitPrice = unitPrice.Value,
                    VatRateId = defaultVatRateId,
                    VatRatePercentage = defaultVatPercentage,
                    VatRegime = vatRegime,
                    ReverseChargeCodeId = reverseChargeCodeId
                });

                orderIndex++;
            }

            return new ItemParseResult { Items = items };
        }
        catch (JsonException)
        {
            return new ItemParseResult
            {
                Error = ChatToolResult.Failure(
                    "Could not parse items JSON. " +
                    "Expected format: [{\"description\": \"Product\", \"quantity\": 1, \"unit_price\": 100}]")
            };
        }
    }

    /// <summary>
    /// Safely extracts a string property from a JSON element.
    /// Returns null if the property doesn't exist or is not a string.
    /// </summary>
    private static string? GetJsonString(JsonElement element, string propertyName)
    {
        if (element.TryGetProperty(propertyName, out var prop))
        {
            return prop.ValueKind == JsonValueKind.String
                ? prop.GetString()
                : prop.ToString(); // Handles numbers formatted as strings.
        }
        return null;
    }

    /// <summary>
    /// Safely extracts a decimal property from a JSON element.
    /// Handles both number and string representations (AI sometimes quotes numbers).
    /// </summary>
    private static decimal? GetJsonDecimal(JsonElement element, string propertyName)
    {
        if (!element.TryGetProperty(propertyName, out var prop))
            return null;

        if (prop.ValueKind == JsonValueKind.Number)
            return prop.GetDecimal();

        if (prop.ValueKind == JsonValueKind.String &&
            decimal.TryParse(prop.GetString(), out var parsed))
            return parsed;

        return null;
    }

    internal record ItemParseResult
    {
        public List<CreateInvoiceItemDto>? Items { get; init; }
        public ChatToolResult? Error { get; init; }
    }
}

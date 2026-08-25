using Fakvio.Application.Service;
using Microsoft.Extensions.Logging;

namespace Fakvio.Infrastructure.Service.ChatTools;

/// <summary>
/// Chat tool that lists the tenant's VAT rates — the "DPH sazby" from story #149, issue #224.
///
/// Typical usage:
///   "Jaké máme sazby DPH?"
///   "Která sazba je výchozí snížená?"
///
/// Read-only, so no confirmation step (DEVGUIDE §4.7).
/// </summary>
public class ListVatRatesTool : IChatTool
{
    private readonly IVatRateService _vatRateService;
    private readonly ILogger<ListVatRatesTool> _logger;

    public ListVatRatesTool(IVatRateService vatRateService, ILogger<ListVatRatesTool> logger)
    {
        _vatRateService = vatRateService;
        _logger = logger;
    }

    public string ToolName => "list_vat_rates";

    public string Description =>
        "List the VAT rates (sazby DPH) configured for the user's company: percentage, name, " +
        "whether the rate is reduced or standard, its validity period and which rates are the " +
        "default ones. Returns the IDs needed to change a rate.";

    /// <summary>Parameter schema — static because it never changes per instance.</summary>
    private static readonly ChatToolParameter[] Schema =
    [
        new()
        {
            Name = "include_inactive",
            Type = ChatToolParameterType.Boolean,
            Description = "True to include deactivated rates (default false)"
        }
    ];

    public IReadOnlyList<ChatToolParameter> Parameters => Schema;

    public async Task<ChatToolResult> ExecuteAsync(
        Dictionary<string, string> parameters,
        CancellationToken ct = default)
    {
        _logger.LogInformation("ListVatRatesTool executing");

        var includeInactive = SettingsChatToolSupport.OptionalFlag(parameters, "include_inactive") ?? false;

        var rates = await _vatRateService.GetAllVatRatesAsync(includeInactive, ct);

        return ChatToolResult.Success(SettingsChatToolSupport.FormatRates(rates));
    }
}

using Fakvio.Application.Service;
using Fakvio.Contracts.Dto.Vies;
using Microsoft.Extensions.Logging;

namespace Fakvio.Infrastructure.Service.ChatTools;

/// <summary>
/// Chat tool that verifies an EU VAT identification number (DIČ) against VIES.
///
/// Complements <see cref="AresLookupTool"/>: ARES looks up a Czech company by IČO, this tool
/// checks whether any EU VAT ID (including non-Czech ones) is currently registered — useful
/// before invoicing an EU client under the reverse-charge regime.
///
/// Read-only, safe to call repeatedly — successful results are cached by <see cref="IViesService"/>.
/// </summary>
public class VerifyVatViesTool : IChatTool
{
    private readonly IViesService _viesService;
    private readonly ILogger<VerifyVatViesTool> _logger;

    public VerifyVatViesTool(IViesService viesService, ILogger<VerifyVatViesTool> logger)
    {
        _viesService = viesService;
        _logger = logger;
    }

    public string ToolName => "verify_vat_vies";

    public string Description =>
        "Verifies an EU VAT identification number (DIČ) against VIES (EU VAT registry). " +
        "Returns whether the VAT ID is currently registered, plus the registered name/address " +
        "when the member state releases them. Works for any EU country, not just Czech IČO " +
        "(use ares_lookup for that). Read-only.";

    private static readonly ChatToolParameter[] Schema =
    [
        new()
        {
            Name = "vat_id",
            Type = ChatToolParameterType.String,
            Description = "EU VAT ID including the 2-letter country prefix, e.g. 'CZ12345678' or 'DE123456789'",
            IsRequired = true
        }
    ];

    public IReadOnlyList<ChatToolParameter> Parameters => Schema;

    public async Task<ChatToolResult> ExecuteAsync(
        Dictionary<string, string> parameters,
        CancellationToken ct = default)
    {
        var vatId = parameters["vat_id"];

        _logger.LogInformation("VIES verification tool executing (VAT ID length {Length})", vatId.Length);

        var result = await _viesService.VerifyAsync(vatId, ct);

        if (result.Status == EViesCheckStatus.Unavailable)
        {
            return ChatToolResult.Failure(
                $"Could not verify VAT ID — VIES service is currently unavailable: {result.ErrorMessage}");
        }

        if (result.Status == EViesCheckStatus.Invalid)
        {
            return ChatToolResult.Success(
                $"VAT ID {result.CountryCode}{result.VatNumber} is NOT valid/registered in VIES." +
                (result.ErrorMessage is not null ? $" ({result.ErrorMessage})" : ""));
        }

        var output =
            $"VIES Verification Result:\n" +
            $"- VAT ID: {result.CountryCode}{result.VatNumber}\n" +
            $"- Status: Valid (registered)\n" +
            $"- Name: {result.Name ?? "Not released by member state"}\n" +
            $"- Address: {result.Address ?? "Not released by member state"}";

        return ChatToolResult.Success(output);
    }
}

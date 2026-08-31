using AresService;
using Fakvio.Application.Service;
using Microsoft.Extensions.Logging;

namespace Fakvio.Infrastructure.Service.ChatTools;

/// <summary>
/// Chat tool that looks up a Czech company in the ARES (Administrative Register
/// of Economic Subjects) by its registration number (IČO).
///
/// When a user asks something like "Najdi firmu 12345678" or "ARES lookup IČO 12345678",
/// this tool fetches the company info and returns formatted text for the AI to present.
///
/// Reuses the existing IAresService which handles caching (30-day TTL),
/// input validation, and the ARES REST API communication.
///
/// Junior note: This is a read-only tool — it fetches data but doesn't create anything.
/// Safe to call multiple times (idempotent, cached).
/// </summary>
public class AresLookupTool : IChatTool
{
    private readonly IAresService _aresService;
    private readonly ILogger<AresLookupTool> _logger;

    public AresLookupTool(IAresService aresService, ILogger<AresLookupTool> logger)
    {
        _aresService = aresService;
        _logger = logger;
    }

    /// <summary>
    /// Tool identity — used in the system prompt and JSON tool call format.
    /// </summary>
    public string ToolName => "ares_lookup";

    public string Description =>
        "Looks up a Czech company in the ARES business registry by IČO (registration number). " +
        "Returns company name, tax number (DIČ), VAT status, and registered address. " +
        "Read-only — use it when unsure whether the user wants a lookup or a new client.";

    /// <summary>
    /// Parameter schema — static because it never changes per instance.
    /// </summary>
    private static readonly ChatToolParameter[] Schema =
    [
        new()
        {
            Name = "registration_number",
            Type = ChatToolParameterType.String,
            Description = "Czech company registration number (IČO), exactly 8 digits",
            IsRequired = true
        }
    ];

    public IReadOnlyList<ChatToolParameter> Parameters => Schema;

    /// <summary>
    /// Executes the ARES lookup.
    /// Returns formatted company info on success, or an error message on failure.
    /// </summary>
    public async Task<ChatToolResult> ExecuteAsync(
        Dictionary<string, string> parameters,
        CancellationToken ct = default)
    {
        // Presence of the required parameter is guaranteed by ChatToolExecutor's central validation.
        // Strip any whitespace or formatting the AI might have added (e.g., "123 456 78").
        var ico = parameters["registration_number"].Trim().Replace(" ", "");

        _logger.LogInformation("ARES lookup tool executing for IČO {Ico}", ico);

        // Call the existing ARES service (handles validation, caching, and the ARES REST API).
        var companyInfo = await _aresService.GetCompanyInfoAsync(ico, ct);

        if (!companyInfo.IsSuccessful)
        {
            _logger.LogWarning("ARES lookup failed for IČO {Ico}: {Error}", ico, companyInfo.ErrorMessage);
            return ChatToolResult.Failure(
                companyInfo.ErrorMessage ?? $"Company with IČO {ico} not found in ARES registry.");
        }

        // Format the result as a readable text block for the AI to present to the user.
        var address = companyInfo.Address != null
            ? $"{companyInfo.Address.Street}, {companyInfo.Address.City}, {companyInfo.Address.PostalCode}"
            : "Address not available";

        var output =
            $"ARES Lookup Result:\n" +
            $"- Company name: {companyInfo.CompanyName}\n" +
            $"- IČO: {companyInfo.RegistrationNumber}\n" +
            $"- DIČ: {companyInfo.TaxNumber ?? "Not a VAT payer"}\n" +
            $"- VAT payer: {(companyInfo.IsVatPayer ? "Yes" : "No")}\n" +
            $"- Legal form: {companyInfo.LegalForm ?? "Unknown"}\n" +
            $"- Address: {address}";

        _logger.LogInformation("ARES lookup successful for {CompanyName} (IČO {Ico})",
            companyInfo.CompanyName, ico);

        return ChatToolResult.Success(output);
    }
}

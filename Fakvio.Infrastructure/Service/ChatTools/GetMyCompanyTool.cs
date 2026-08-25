using Fakvio.Application.Service;
using Microsoft.Extensions.Logging;

namespace Fakvio.Infrastructure.Service.ChatTools;

/// <summary>
/// Chat tool that reads the tenant's own company — the issuer. Read-only.
///
/// Typical usage:
///   "Jaké mám nastavení firmy?"
///   "Jaké je naše číslo účtu?"
///   "Jsme plátci DPH?"
///
/// Takes no parameters: a tenant has exactly one issuer, so there is nothing to choose.
/// It is also the tool that hands the model the bank account IDs that
/// <see cref="UpdateBankAccountTool"/> and <see cref="DeleteBankAccountTool"/> need.
/// </summary>
public class GetMyCompanyTool : IChatTool
{
    private readonly IClientService _clientService;
    private readonly ILogger<GetMyCompanyTool> _logger;

    public GetMyCompanyTool(IClientService clientService, ILogger<GetMyCompanyTool> logger)
    {
        _clientService = clientService;
        _logger = logger;
    }

    public string ToolName => "get_my_company";

    public string Description =>
        "Get the settings of the user's own company (the issuer that appears as the sender on " +
        "invoices): name, IČO, DIČ, VAT payer status, document language, addresses, contacts " +
        "and bank accounts including their IDs. Read-only — call it before changing anything, " +
        "and to answer questions about our own company or our bank account numbers.";

    /// <summary>No parameters — see the class summary.</summary>
    public IReadOnlyList<ChatToolParameter> Parameters => [];

    public async Task<ChatToolResult> ExecuteAsync(
        Dictionary<string, string> parameters,
        CancellationToken ct = default)
    {
        _logger.LogInformation("GetMyCompanyTool executing");

        var issuer = await _clientService.GetIssuerAsync(ct);

        return issuer is null
            ? ChatToolResult.Failure(IssuerChatToolSupport.NoIssuerConfigured)
            : ChatToolResult.Success(IssuerChatToolSupport.FormatCompany(issuer));
    }
}

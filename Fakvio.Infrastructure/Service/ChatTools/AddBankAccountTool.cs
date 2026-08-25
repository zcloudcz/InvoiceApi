using Fakvio.Application.Service;
using Fakvio.Contracts.Dto.Client;
using Microsoft.Extensions.Logging;

namespace Fakvio.Infrastructure.Service.ChatTools;

/// <summary>
/// Chat tool that adds a bank account to the tenant's own company — the issuer (issue #220).
/// This is the "číslo bankovního účtu si uživatel nastaví přes asistenta" case from story #149.
///
/// Typical usage:
///   "Přidej nám účet 1234567890/0100."
///   "Máme nový eurový účet, IBAN CZ65 0800 0000 0019 2000 0145, ať je výchozí."
///
/// Data-changing, therefore <see cref="IConfirmableChatTool"/> — nothing is written before the
/// user approves the preview (DEVGUIDE §4.7).
/// </summary>
public class AddBankAccountTool : IConfirmableChatTool
{
    private readonly IClientService _clientService;
    private readonly ILogger<AddBankAccountTool> _logger;

    public AddBankAccountTool(IClientService clientService, ILogger<AddBankAccountTool> logger)
    {
        _clientService = clientService;
        _logger = logger;
    }

    public string ToolName => "add_bank_account";

    public string Description =>
        "Add a bank account to the user's own company (the issuer). The account number is " +
        "required, everything else is optional. The very first account of the company always " +
        "becomes the default one — the account offered on new invoices and used for QR payments.";

    /// <summary>Parameter schema — static because it never changes per instance.</summary>
    private static readonly ChatToolParameter[] Schema =
    [
        new()
        {
            Name = "account_number",
            Type = ChatToolParameterType.String,
            Description = "Bank account number, Czech format '1234567890/0100'",
            IsRequired = true
        },
        new()
        {
            Name = "label",
            Type = ChatToolParameterType.String,
            Description = "Short label that tells the accounts apart, e.g. 'CZK účet'"
        },
        new()
        {
            Name = "bank_name",
            Type = ChatToolParameterType.String,
            Description = "Bank name, e.g. 'Fio banka'"
        },
        new()
        {
            Name = "iban",
            Type = ChatToolParameterType.String,
            Description = "IBAN for international payments"
        },
        new()
        {
            Name = "swift",
            Type = ChatToolParameterType.String,
            Description = "SWIFT/BIC code for international transfers"
        },
        new()
        {
            Name = "currency_code",
            Type = ChatToolParameterType.String,
            Description = "Currency of the account as a 3-letter code, e.g. CZK or EUR"
        },
        new()
        {
            Name = "is_default",
            Type = ChatToolParameterType.Boolean,
            Description = "True to make this the default account, replacing the current default"
        }
    ];

    public IReadOnlyList<ChatToolParameter> Parameters => Schema;

    /// <summary>
    /// Describes the account that would be added, and — because this is the one bit the user
    /// cannot see coming — whether it will end up as the default account.
    /// </summary>
    public async Task<ChatToolResult> BuildPreviewAsync(
        Dictionary<string, string> parameters,
        CancellationToken ct = default)
    {
        var issuer = await _clientService.GetIssuerAsync(ct);
        if (issuer is null)
            return ChatToolResult.Failure(IssuerChatToolSupport.NoIssuerConfigured);

        var newAccount = BuildAccount(parameters);

        // ClientService.AddBankAccountAsync makes the first account of a company default no
        // matter what was asked for; after that, only an explicit is_default does.
        var becomesDefault = issuer.BankAccount.Count == 0 || newAccount.IsDefault;
        var currentDefault = issuer.BankAccount.FirstOrDefault(account => account.IsDefault);

        var preview =
            $"A new bank account would be added to '{issuer.CompanyName}':\n" +
            $"  {Describe(newAccount)}\n" +
            (becomesDefault
                ? "  It would become the DEFAULT account" +
                  (currentDefault is null
                      ? " (the company has none yet).\n"
                      : $", replacing '{currentDefault.AccountNumber}'.\n")
                : "  The current default account stays unchanged.\n") +
            $"The company currently has {issuer.BankAccount.Count} bank account(s).";

        return ChatToolResult.Success(preview);
    }

    public async Task<ChatToolResult> ExecuteAsync(
        Dictionary<string, string> parameters,
        CancellationToken ct = default)
    {
        _logger.LogInformation("AddBankAccountTool executing");

        // Re-read the issuer: preview and execution are separate calls with anything in
        // between, so nothing from the preview may be assumed to still hold.
        var issuer = await _clientService.GetIssuerAsync(ct);
        if (issuer is null)
            return ChatToolResult.Failure(IssuerChatToolSupport.NoIssuerConfigured);

        var updated = await _clientService.AddBankAccountAsync(issuer.Id, BuildAccount(parameters), ct);
        if (updated is null)
            return ChatToolResult.Failure(
                $"The bank account could not be added because the company profile (ID {issuer.Id}) no longer exists.");

        _logger.LogInformation("Bank account added to company {ClientId}", issuer.Id);

        return ChatToolResult.Success(
            $"Bank account added.\n{IssuerChatToolSupport.FormatAccounts(updated.BankAccount)}");
    }

    /// <summary>
    /// Builds the DTO from the parameters. Shared by the preview and the write so the preview
    /// describes exactly the record that would be stored.
    /// </summary>
    private static CreateBankAccountDto BuildAccount(Dictionary<string, string> parameters)
        => new()
        {
            // Required parameter — presence is guaranteed by the executor's central validation.
            AccountNumber = parameters["account_number"].Trim(),
            Label = IssuerChatToolSupport.OptionalText(parameters, "label"),
            BankName = IssuerChatToolSupport.OptionalText(parameters, "bank_name"),
            IBAN = IssuerChatToolSupport.OptionalText(parameters, "iban"),
            SWIFT = IssuerChatToolSupport.OptionalText(parameters, "swift"),
            CurrencyCode = IssuerChatToolSupport.OptionalText(parameters, "currency_code")?.ToUpperInvariant(),
            IsDefault = IssuerChatToolSupport.OptionalFlag(parameters, "is_default") ?? false
        };

    /// <summary>
    /// Renders the not-yet-stored account with the same wording the stored ones get, minus the
    /// id it does not have yet.
    /// </summary>
    private static string Describe(CreateBankAccountDto account)
        => IssuerChatToolSupport.DescribeAccount(
            new BankAccountDto
            {
                Label = account.Label,
                BankName = account.BankName,
                AccountNumber = account.AccountNumber,
                IBAN = account.IBAN,
                SWIFT = account.SWIFT,
                CurrencyCode = account.CurrencyCode,
                IsDefault = account.IsDefault
            },
            includeId: false);
}

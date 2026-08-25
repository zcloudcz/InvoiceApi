using Fakvio.Application.Service;
using Fakvio.Contracts.Dto.Client;
using Microsoft.Extensions.Logging;

namespace Fakvio.Infrastructure.Service.ChatTools;

/// <summary>
/// Chat tool that changes one bank account of the tenant's own company — the issuer (issue #220).
///
/// Typical usage:
///   "Oprav u účtu 3 IBAN na CZ65 0800 0000 0019 2000 0145."
///   "Ať je eurový účet výchozí."
///
/// The account is addressed by the ID that <c>get_my_company</c> prints. Data-changing,
/// therefore <see cref="IConfirmableChatTool"/> — nothing is written before the user approves
/// the preview (DEVGUIDE §4.7).
/// </summary>
public class UpdateBankAccountTool : IConfirmableChatTool
{
    private readonly IClientService _clientService;
    private readonly ILogger<UpdateBankAccountTool> _logger;

    public UpdateBankAccountTool(IClientService clientService, ILogger<UpdateBankAccountTool> logger)
    {
        _clientService = clientService;
        _logger = logger;
    }

    public string ToolName => "update_bank_account";

    public string Description =>
        "Change one bank account of the user's own company (the issuer): its number, label, " +
        "bank, IBAN, SWIFT, currency, or which account is the default one. Identify the account " +
        "by the ID returned from get_my_company and send only the fields that should change.";

    /// <summary>Parameter schema — static because it never changes per instance.</summary>
    private static readonly ChatToolParameter[] Schema =
    [
        new()
        {
            Name = "bank_account_id",
            Type = ChatToolParameterType.Integer,
            Description = "ID of the bank account to change, as returned by get_my_company",
            IsRequired = true
        },
        new()
        {
            Name = "account_number",
            Type = ChatToolParameterType.String,
            Description = "New bank account number, Czech format '1234567890/0100'"
        },
        new()
        {
            Name = "label",
            Type = ChatToolParameterType.String,
            Description = "New short label of the account, e.g. 'CZK účet'"
        },
        new()
        {
            Name = "bank_name",
            Type = ChatToolParameterType.String,
            Description = "New bank name, e.g. 'Fio banka'"
        },
        new()
        {
            Name = "iban",
            Type = ChatToolParameterType.String,
            Description = "New IBAN for international payments"
        },
        new()
        {
            Name = "swift",
            Type = ChatToolParameterType.String,
            Description = "New SWIFT/BIC code"
        },
        new()
        {
            Name = "currency_code",
            Type = ChatToolParameterType.String,
            Description = "New currency of the account as a 3-letter code, e.g. CZK or EUR"
        },
        new()
        {
            Name = "is_default",
            Type = ChatToolParameterType.Boolean,
            Description = "True to make this the default account of the company"
        }
    ];

    public IReadOnlyList<ChatToolParameter> Parameters => Schema;

    public async Task<ChatToolResult> BuildPreviewAsync(
        Dictionary<string, string> parameters,
        CancellationToken ct = default)
    {
        var (issuer, target, failure) = await ResolveAccountAsync(parameters, ct);
        if (failure is not null)
            return failure;

        var changes = DescribeChanges(target!, parameters);
        if (changes.Count == 0)
            return ChatToolResult.Failure(NothingToChange);

        return ChatToolResult.Success(
            $"Bank account ID={target!.Id} of '{issuer!.CompanyName}' would change:\n" +
            string.Join("\n", changes.Select(change => $"  - {change}")));
    }

    public async Task<ChatToolResult> ExecuteAsync(
        Dictionary<string, string> parameters,
        CancellationToken ct = default)
    {
        _logger.LogInformation("UpdateBankAccountTool executing for account {BankAccountId}",
            IssuerChatToolSupport.RequiredId(parameters, "bank_account_id"));

        // Re-read: preview and execution are separate calls and nothing correlates them, so the
        // account may have been changed or removed in between.
        var (issuer, target, failure) = await ResolveAccountAsync(parameters, ct);
        if (failure is not null)
            return failure;

        var changes = DescribeChanges(target!, parameters);
        if (changes.Count == 0)
            return ChatToolResult.Failure(NothingToChange);

        var accounts = BuildReplacementList(issuer!, target!, parameters);

        return await IssuerChatToolSupport.SaveBankAccountsAsync(
            _clientService,
            issuer!,
            accounts,
            $"Bank account updated ({string.Join("; ", changes)}). The accounts were re-saved, " +
            "so their IDs are new — use the ones below from now on.",
            ct);
    }

    /// <summary>Message used when the model names an account but no field to change on it.</summary>
    private const string NothingToChange =
        "No change was requested. Send at least one field to change (for example account_number, " +
        "iban or is_default).";

    /// <summary>
    /// Loads the issuer and the addressed account, or produces the failure that explains which
    /// of the two is missing. Shared by the preview and the write so both fail identically.
    /// </summary>
    private async Task<(ClientDto? Issuer, BankAccountDto? Account, ChatToolResult? Failure)> ResolveAccountAsync(
        Dictionary<string, string> parameters,
        CancellationToken ct)
    {
        var issuer = await _clientService.GetIssuerAsync(ct);
        if (issuer is null)
            return (null, null, ChatToolResult.Failure(IssuerChatToolSupport.NoIssuerConfigured));

        var bankAccountId = IssuerChatToolSupport.RequiredId(parameters, "bank_account_id");
        var account = issuer.BankAccount.FirstOrDefault(candidate => candidate.Id == bankAccountId);

        return account is null
            ? (issuer, null, ChatToolResult.Failure(
                IssuerChatToolSupport.DescribeUnknownAccount(bankAccountId, issuer.BankAccount)))
            : (issuer, account, null);
    }

    /// <summary>
    /// Lists the fields that would actually change, "before → after". A parameter that repeats
    /// the stored value is not a change — otherwise the preview would announce an edit that
    /// leaves the record exactly as it was.
    /// </summary>
    private static List<string> DescribeChanges(BankAccountDto account, Dictionary<string, string> parameters)
    {
        var changes = new List<string>();

        AddIfChanged(changes, "account number", account.AccountNumber, Text(parameters, "account_number"));
        AddIfChanged(changes, "label", account.Label, Text(parameters, "label"));
        AddIfChanged(changes, "bank", account.BankName, Text(parameters, "bank_name"));
        AddIfChanged(changes, "IBAN", account.IBAN, Text(parameters, "iban"));
        AddIfChanged(changes, "SWIFT", account.SWIFT, Text(parameters, "swift"));
        AddIfChanged(changes, "currency", account.CurrencyCode, CurrencyCode(parameters));

        var isDefault = IssuerChatToolSupport.OptionalFlag(parameters, "is_default");
        if (isDefault is not null && isDefault != account.IsDefault)
        {
            changes.Add(isDefault.Value
                ? "default account: this one becomes the default"
                : "default account: this one stops being the default");
        }

        return changes;
    }

    private static void AddIfChanged(List<string> changes, string label, string? current, string? supplied)
    {
        if (supplied is not null && supplied != current)
            changes.Add($"{label}: '{current ?? "(not set)"}' → '{supplied}'");
    }

    /// <summary>Shorthand for "the optional text parameter, or null when it was not sent".</summary>
    private static string? Text(Dictionary<string, string> parameters, string name)
        => IssuerChatToolSupport.OptionalText(parameters, name);

    /// <summary>
    /// The currency code, uppercased — so that "czk" against a stored "CZK" is not reported
    /// (and saved) as a change.
    /// </summary>
    private static string? CurrencyCode(Dictionary<string, string> parameters)
        => IssuerChatToolSupport.OptionalText(parameters, "currency_code")?.ToUpperInvariant();

    /// <summary>
    /// Builds the full account collection that will be saved: every account as it is stored
    /// today, with the addressed one carrying the new values.
    /// </summary>
    private static List<UpdateBankAccountDto> BuildReplacementList(
        ClientDto issuer,
        BankAccountDto target,
        Dictionary<string, string> parameters)
    {
        var accounts = IssuerChatToolSupport.ToUpdateList(issuer.BankAccount);
        var index = issuer.BankAccount.IndexOf(target);
        var edited = accounts[index];

        edited.AccountNumber = Text(parameters, "account_number") ?? edited.AccountNumber;
        edited.Label = Text(parameters, "label") ?? edited.Label;
        edited.BankName = Text(parameters, "bank_name") ?? edited.BankName;
        edited.IBAN = Text(parameters, "iban") ?? edited.IBAN;
        edited.SWIFT = Text(parameters, "swift") ?? edited.SWIFT;
        edited.CurrencyCode = CurrencyCode(parameters) ?? edited.CurrencyCode;

        var isDefault = IssuerChatToolSupport.OptionalFlag(parameters, "is_default");
        if (isDefault is not null)
        {
            edited.IsDefault = isDefault;

            // Only one account may be the default one. ClientService keeps whatever flags it is
            // given when at least one is true, so clearing the others is this tool's job.
            if (isDefault.Value)
            {
                foreach (var other in accounts.Where(candidate => !ReferenceEquals(candidate, edited)))
                    other.IsDefault = false;
            }
        }

        return accounts;
    }
}

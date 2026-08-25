using Fakvio.Application.Service;
using Microsoft.Extensions.Logging;

namespace Fakvio.Infrastructure.Service.ChatTools;

/// <summary>
/// Chat tool that removes one bank account from the tenant's own company — the issuer
/// (issue #220).
///
/// Typical usage:
///   "Zruš u nás ten starý účet 1234567890/0100."
///
/// Destructive and irreversible, therefore <see cref="IConfirmableChatTool"/>: the first call
/// only says what would be deleted. The confirm gate is a UX flow, not an authorization check
/// (see <see cref="IConfirmableChatTool"/>) — deleting a bank account is allowed here because
/// the same user may delete it on the My Company page without any extra permission.
/// </summary>
public class DeleteBankAccountTool : IConfirmableChatTool
{
    private readonly IClientService _clientService;
    private readonly ILogger<DeleteBankAccountTool> _logger;

    public DeleteBankAccountTool(IClientService clientService, ILogger<DeleteBankAccountTool> logger)
    {
        _clientService = clientService;
        _logger = logger;
    }

    public string ToolName => "delete_bank_account";

    public string Description =>
        "Permanently remove one bank account from the user's own company (the issuer). " +
        "Identify the account by the ID returned from get_my_company. Invoices already issued " +
        "keep the payment details printed on them; only the stored account is removed.";

    /// <summary>Parameter schema — static because it never changes per instance.</summary>
    private static readonly ChatToolParameter[] Schema =
    [
        new()
        {
            Name = "bank_account_id",
            Type = ChatToolParameterType.Integer,
            Description = "ID of the bank account to remove, as returned by get_my_company",
            IsRequired = true
        }
    ];

    public IReadOnlyList<ChatToolParameter> Parameters => Schema;

    public async Task<ChatToolResult> BuildPreviewAsync(
        Dictionary<string, string> parameters,
        CancellationToken ct = default)
    {
        var issuer = await _clientService.GetIssuerAsync(ct);
        if (issuer is null)
            return ChatToolResult.Failure(IssuerChatToolSupport.NoIssuerConfigured);

        var bankAccountId = IssuerChatToolSupport.RequiredId(parameters, "bank_account_id");
        var target = issuer.BankAccount.FirstOrDefault(account => account.Id == bankAccountId);
        if (target is null)
            return ChatToolResult.Failure(
                IssuerChatToolSupport.DescribeUnknownAccount(bankAccountId, issuer.BankAccount));

        var remaining = issuer.BankAccount.Count - 1;

        // Two consequences worth spelling out before the user agrees: losing the default account
        // (ClientService then promotes the first remaining one) and losing the last one, which
        // leaves new invoices without payment details and without a QR code.
        var consequence = target.IsDefault && remaining > 0
            ? "\n  It is the DEFAULT account — the first remaining account becomes the new default."
            : remaining == 0
                ? "\n  It is the company's LAST bank account — new invoices would carry no " +
                  "payment details and no QR payment code."
                : string.Empty;

        return ChatToolResult.Success(
            $"This bank account would be permanently removed from '{issuer.CompanyName}':\n" +
            $"  {IssuerChatToolSupport.DescribeAccount(target)}{consequence}\n" +
            $"{remaining} bank account(s) would remain.");
    }

    public async Task<ChatToolResult> ExecuteAsync(
        Dictionary<string, string> parameters,
        CancellationToken ct = default)
    {
        var bankAccountId = IssuerChatToolSupport.RequiredId(parameters, "bank_account_id");

        _logger.LogInformation("DeleteBankAccountTool executing for account {BankAccountId}", bankAccountId);

        // Re-read: preview and execution are separate calls and nothing correlates them, so the
        // account may already be gone — deleting "whatever has this id now" is exactly the
        // mistake a re-read prevents.
        var issuer = await _clientService.GetIssuerAsync(ct);
        if (issuer is null)
            return ChatToolResult.Failure(IssuerChatToolSupport.NoIssuerConfigured);

        var target = issuer.BankAccount.FirstOrDefault(account => account.Id == bankAccountId);
        if (target is null)
            return ChatToolResult.Failure(
                IssuerChatToolSupport.DescribeUnknownAccount(bankAccountId, issuer.BankAccount));

        var remaining = IssuerChatToolSupport.ToUpdateList(
            issuer.BankAccount.Where(account => account.Id != bankAccountId));

        return await IssuerChatToolSupport.SaveBankAccountsAsync(
            _clientService,
            _logger,
            issuer,
            remaining,
            $"Bank account '{target.AccountNumber}' was removed. The remaining accounts were " +
            "re-saved, so their IDs are new — use the ones below from now on.",
            ct);
    }
}

using System.Globalization;
using System.Text;
using Fakvio.Application.Service;
using Fakvio.Contracts.Dto.Client;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Npgsql;

namespace Fakvio.Infrastructure.Service.ChatTools;

/// <summary>
/// Shared plumbing for the five chat tools that read and write the issuer — the tenant's own
/// company (issue #220): <see cref="GetMyCompanyTool"/>, <see cref="UpdateMyCompanyTool"/>,
/// <see cref="AddBankAccountTool"/>, <see cref="UpdateBankAccountTool"/> and
/// <see cref="DeleteBankAccountTool"/>.
///
/// It exists so the five tools describe the same data with the same words. The model only ever
/// sees the text these helpers produce, so a bank account rendered one way in the preview and
/// another way in the result would read to the user as if something else had been changed.
/// </summary>
internal static class IssuerChatToolSupport
{
    /// <summary>
    /// Returned when the tenant has no <c>IsIssuer</c> client at all. Every one of the five
    /// tools starts from the issuer, so all of them fail the same way — and the message says
    /// where the user fixes it instead of just reporting "not found".
    /// </summary>
    public const string NoIssuerConfigured =
        "No company profile is set up in this tenant yet, so there is nothing to read or change. " +
        "The user has to fill in the company first on the My Company page (/my-company).";

    /// <summary>
    /// Reads an optional text parameter, or null when the model did not send it.
    ///
    /// Blank counts as "not sent": models happily send an empty string for a value they do
    /// not have, and treating that as a real value would wipe the stored field.
    ///
    /// Junior note on <c>Trim</c>: ChatToolExecutor validates the trimmed value but dispatches
    /// the raw one, so a stray space from the model would otherwise be written to the database
    /// (see the same note in <see cref="ListInvoicesTool"/>, tracked centrally as #268).
    /// </summary>
    public static string? OptionalText(Dictionary<string, string> parameters, string name)
        => parameters.TryGetValue(name, out var raw) && !string.IsNullOrWhiteSpace(raw)
            ? raw.Trim()
            : null;

    /// <summary>
    /// Reads an optional boolean parameter, or null when it was not sent.
    /// An unparsable value cannot reach this method — the executor rejects it as a type error
    /// before the tool runs — so null really does mean "leave this field alone".
    /// </summary>
    public static bool? OptionalFlag(Dictionary<string, string> parameters, string name)
        => parameters.TryGetValue(name, out var raw) && bool.TryParse(raw.Trim(), out var flag)
            ? flag
            : null;

    /// <summary>
    /// Reads a required whole-number parameter. Presence and the numeric format are guaranteed
    /// by the executor's central validation, so parsing cannot fail here.
    /// InvariantCulture because the model sends JSON numbers, never Czech-formatted ones.
    /// </summary>
    public static long RequiredId(Dictionary<string, string> parameters, string name)
        => long.Parse(parameters[name].Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture);

    /// <summary>
    /// Renders the whole company profile as a text block — one fact per line, because this text
    /// is the model's only source for the answer and anything missing gets invented instead.
    /// </summary>
    public static string FormatCompany(ClientDto issuer)
    {
        var sb = new StringBuilder();
        sb.AppendLine("Company profile (issuer — the company that appears as the sender on invoices):");
        sb.AppendLine($"  Company ID: {issuer.Id}");
        sb.AppendLine($"  Name: {issuer.CompanyName}");

        if (!string.IsNullOrWhiteSpace(issuer.TradingName))
            sb.AppendLine($"  Trading name: {issuer.TradingName}");

        sb.AppendLine($"  IČO: {issuer.RegistrationNumber ?? "(not set)"}");
        sb.AppendLine($"  DIČ: {issuer.TaxNumber ?? "(not set)"}");
        sb.AppendLine($"  VAT payer: {(issuer.IsVatPayer ? "yes" : "no")}");
        sb.AppendLine($"  Document language: {issuer.Language}");

        AppendAddresses(sb, issuer);
        AppendContacts(sb, issuer);

        sb.Append(FormatAccounts(issuer.BankAccount));

        return sb.ToString();
    }

    /// <summary>
    /// Renders the bank accounts, always including the id — it is the handle the update and
    /// delete tools need, and the model can only quote an id it has been shown.
    /// </summary>
    public static string FormatAccounts(IReadOnlyList<BankAccountDto> accounts)
    {
        if (accounts.Count == 0)
            return "  Bank accounts: none yet\n";

        var sb = new StringBuilder();
        sb.AppendLine("  Bank accounts:");
        foreach (var account in accounts)
            sb.AppendLine($"    {DescribeAccount(account)}");

        return sb.ToString();
    }

    /// <summary>
    /// One bank account on one line: id first, then the number, then whatever else is filled in.
    /// Empty optional fields are left out — an empty "IBAN:" label only invites the model to
    /// fill it in from imagination.
    /// </summary>
    /// <param name="account">The account to render.</param>
    /// <param name="includeId">
    /// False for an account that does not exist yet (a preview of one about to be created) —
    /// printing "ID=0" would offer the model a handle that leads nowhere.
    /// </param>
    public static string DescribeAccount(BankAccountDto account, bool includeId = true)
    {
        var details = new List<string> { $"account: {account.AccountNumber}" };

        if (!string.IsNullOrWhiteSpace(account.Label))
            details.Add($"label: {account.Label}");
        if (!string.IsNullOrWhiteSpace(account.BankName))
            details.Add($"bank: {account.BankName}");
        if (!string.IsNullOrWhiteSpace(account.IBAN))
            details.Add($"IBAN: {account.IBAN}");
        if (!string.IsNullOrWhiteSpace(account.SWIFT))
            details.Add($"SWIFT: {account.SWIFT}");
        if (!string.IsNullOrWhiteSpace(account.CurrencyCode))
            details.Add($"currency: {account.CurrencyCode}");
        if (account.IsDefault)
            details.Add("default");

        return includeId
            ? $"ID={account.Id} | {string.Join(" | ", details)}"
            : string.Join(" | ", details);
    }

    /// <summary>
    /// Explains that the requested bank account id does not belong to this company, and lists
    /// the ids that do — so the model can correct the call instead of guessing again.
    /// </summary>
    public static string DescribeUnknownAccount(long bankAccountId, IReadOnlyList<BankAccountDto> accounts)
        => accounts.Count == 0
            ? $"The company has no bank accounts, so there is no account with ID {bankAccountId}."
            : $"The company has no bank account with ID {bankAccountId}. Existing IDs: " +
              $"{string.Join(", ", accounts.Select(account => account.Id))}.";

    /// <summary>
    /// Maps the stored accounts onto the update DTO used by the replace-all save below.
    /// Every field is carried over on purpose: the save replaces the whole collection, so a
    /// field left out here would be silently erased from an account nobody asked to change.
    /// </summary>
    public static List<UpdateBankAccountDto> ToUpdateList(IEnumerable<BankAccountDto> accounts)
        => accounts.Select(account => new UpdateBankAccountDto
        {
            Label = account.Label,
            BankName = account.BankName,
            AccountNumber = account.AccountNumber,
            IBAN = account.IBAN,
            SWIFT = account.SWIFT,
            CurrencyCode = account.CurrencyCode,
            IsDefault = account.IsDefault
        }).ToList();

    /// <summary>
    /// Persists a rewritten bank account collection and renders the result.
    ///
    /// Why the whole list and not just the one account: <c>IClientService</c> has no
    /// per-account update or delete. Editing bank accounts means sending the full collection to
    /// <c>UpdateClientAsync</c>, which clears the stored accounts and re-inserts the supplied
    /// ones — the very same call the My Company page makes when the user presses Save.
    ///
    /// Two consequences the caller must know about:
    /// <list type="bullet">
    /// <item>The accounts get NEW ids. That is why the result always prints the fresh list —
    /// an id the model remembers from before the call is stale.</item>
    /// <item>An account that is already referenced by payment data (bank transactions, an
    /// incoming-payment mailbox) cannot be re-inserted: those foreign keys are
    /// <c>DeleteBehavior.Restrict</c>, so the database refuses the delete and the whole save
    /// fails. It is translated below into a sentence the user can act on, instead of a raw
    /// driver error.</item>
    /// </list>
    ///
    /// The <paramref name="logger"/> is the calling tool's own logger, so the log entry carries
    /// the tool name in its category. It has to be passed in: this failure is caught here and
    /// never reaches <c>ChatToolExecutor</c>, which is the only other place that logs it.
    /// </summary>
    public static async Task<ChatToolResult> SaveBankAccountsAsync(
        IClientService clientService,
        ILogger logger,
        ClientDto issuer,
        List<UpdateBankAccountDto> accounts,
        string successHeading,
        CancellationToken ct)
    {
        try
        {
            var updated = await clientService.UpdateClientAsync(
                issuer.Id,
                new UpdateClientDto { BankAccount = accounts },
                ct);

            if (updated is null)
                return ChatToolResult.Failure(
                    $"The company profile (ID {issuer.Id}) could not be updated because it no longer exists.");

            return ChatToolResult.Success($"{successHeading}\n{FormatAccounts(updated.BankAccount)}");
        }
        catch (DbUpdateException ex)
        {
            // Log first: the raw driver text belongs on the server, not in a chat reply that also
            // travels to the external LLM provider (it names schema, table and constraint).
            logger.LogError(ex, "Saving the bank accounts of issuer {IssuerId} failed", issuer.Id);

            // Only a foreign-key violation is really "the account is in use". A truncated
            // currency code or a dropped connection is not, and telling the user otherwise sends
            // them looking for payments that do not exist.
            return ChatToolResult.Failure(IsForeignKeyViolation(ex)
                ? "The bank accounts could not be saved because at least one of them is already " +
                  "referenced by payment data (bank transactions or an incoming-payment mailbox). " +
                  "Such an account cannot be rewritten — the user has to detach that data first."
                : "The bank accounts could not be saved because the database rejected the change. " +
                  "The details are in the server log; the user can try again, or edit the accounts " +
                  "on the My Company page (/my-company).");
        }
    }

    /// <summary>
    /// True when the save failed on a foreign key — in practice an account pinned by a bank
    /// transaction or an incoming-payment mailbox (<c>DeleteBehavior.Restrict</c>).
    /// SQLSTATE 23503 is the PostgreSQL code for it.
    /// </summary>
    private static bool IsForeignKeyViolation(DbUpdateException ex)
        => ex.GetBaseException() is PostgresException
        {
            SqlState: PostgresErrorCodes.ForeignKeyViolation
        };

    /// <summary>Appends the registered addresses, primary one first.</summary>
    private static void AppendAddresses(StringBuilder sb, ClientDto issuer)
    {
        if (issuer.Address.Count == 0)
        {
            sb.AppendLine("  Address: not set");
            return;
        }

        sb.AppendLine("  Addresses:");
        foreach (var address in issuer.Address.OrderByDescending(a => a.IsPrimary))
        {
            var line2 = string.IsNullOrWhiteSpace(address.AddressLine2) ? string.Empty : $", {address.AddressLine2}";
            sb.AppendLine(
                $"    {address.AddressType}{(address.IsPrimary ? " (primary)" : string.Empty)}: " +
                $"{address.Street}{line2}, {address.PostalCode} {address.City}, {address.Country}");
        }
    }

    /// <summary>Appends the registered contacts (e-mail, phone, …), primary one first.</summary>
    private static void AppendContacts(StringBuilder sb, ClientDto issuer)
    {
        if (issuer.Contact.Count == 0)
            return;

        sb.AppendLine("  Contacts:");
        foreach (var contact in issuer.Contact.OrderByDescending(c => c.IsPrimary))
        {
            var label = string.IsNullOrWhiteSpace(contact.Label) ? string.Empty : $" ({contact.Label})";
            sb.AppendLine(
                $"    {contact.ContactType}{label}: {contact.ContactValue}" +
                $"{(contact.IsPrimary ? " (primary)" : string.Empty)}");
        }
    }
}

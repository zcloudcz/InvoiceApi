using System.Text;
using Fakvio.Application.Service;
using Fakvio.Contracts.Dto.Client;
using Microsoft.Extensions.Logging;

namespace Fakvio.Infrastructure.Service.ChatTools;

/// <summary>
/// Chat tool that returns the full detail of ONE client: identity, addresses, contacts,
/// bank accounts and billing settings.
///
/// Answers questions like:
///   "Jakou má Alfa s.r.o. adresu?"
///   "Na jaký e-mail posíláme faktury klientovi 42?"
///   "Jaká je splatnost u klienta s IČO 12345678?"
///
/// <see cref="ListClientsTool"/> shows one line per client; this shows everything about one.
/// </summary>
public class GetClientTool : IChatTool
{
    private readonly IClientService _clientService;
    private readonly ILogger<GetClientTool> _logger;

    public GetClientTool(IClientService clientService, ILogger<GetClientTool> logger)
    {
        _clientService = clientService;
        _logger = logger;
    }

    public string ToolName => "get_client";

    public string Description =>
        "Get the full detail of one client: addresses, contacts, bank accounts and billing " +
        "settings. Identify the client by id, registration_number (IČO) or name.";

    public IReadOnlyList<ChatToolParameter> Parameters => ClientLookup.IdentityParameters;

    public async Task<ChatToolResult> ExecuteAsync(
        Dictionary<string, string> parameters,
        CancellationToken ct = default)
    {
        var resolution = await ClientLookup.ResolveAsync(_clientService, parameters, ct);
        if (resolution.Error is not null)
            return resolution.Error;

        var client = resolution.Client!;
        _logger.LogInformation("GetClientTool: returning detail of client {ClientId}", client.Id);

        return ChatToolResult.Success(Format(client));
    }

    /// <summary>
    /// Formats the client as a text block for the AI to present.
    ///
    /// Sections with no data are written as "(none)" rather than left out: a missing e-mail
    /// is usually the answer the user is looking for ("why didn't the invoice arrive?"), and
    /// an omitted heading reads to the model like data it forgot to ask for.
    /// </summary>
    private static string Format(ClientDto client)
    {
        var sb = new StringBuilder();

        sb.AppendLine($"Client detail (ID={client.Id}):");
        sb.AppendLine($"- Name: {client.CompanyName}");
        if (!string.IsNullOrWhiteSpace(client.TradingName))
            sb.AppendLine($"- Trading name: {client.TradingName}");
        sb.AppendLine($"- IČO: {client.RegistrationNumber ?? "(none)"}");
        sb.AppendLine($"- DIČ: {client.TaxNumber ?? "(none)"}");
        sb.AppendLine($"- VAT payer: {ClientLookup.YesNo(client.IsVatPayer)}");
        sb.AppendLine($"- Active: {ClientLookup.YesNo(client.IsActive)}");
        sb.AppendLine($"- Issuer (your own company): {ClientLookup.YesNo(client.IsIssuer)}");
        sb.AppendLine($"- Document language: {client.Language}");
        sb.AppendLine($"- Last ARES refresh: {ChatToolDates.Format(client.LastAresFetchDate)}");

        AppendSection(sb, "Addresses", client.Address,
            address => $"{address.AddressType}: {address.Street}, {address.PostalCode} {address.City}, " +
                       $"{address.Country}{(address.IsPrimary ? " (primary)" : string.Empty)}");

        AppendSection(sb, "Contacts", client.Contact,
            contact => $"{contact.ContactType}: {contact.ContactValue}" +
                       $"{(string.IsNullOrWhiteSpace(contact.Label) ? string.Empty : $" ({contact.Label})")}" +
                       $"{(contact.IsPrimary ? " (primary)" : string.Empty)}");

        AppendSection(sb, "Bank accounts", client.BankAccount,
            account => $"{account.AccountNumber}" +
                       $"{(string.IsNullOrWhiteSpace(account.BankName) ? string.Empty : $" ({account.BankName})")}" +
                       $"{(string.IsNullOrWhiteSpace(account.IBAN) ? string.Empty : $", IBAN {account.IBAN}")}" +
                       $"{(string.IsNullOrWhiteSpace(account.CurrencyCode) ? string.Empty : $", {account.CurrencyCode}")}" +
                       $"{(account.IsDefault ? " (default)" : string.Empty)}");

        sb.AppendLine("Billing settings:");
        if (client.BillingSettings is null)
        {
            sb.AppendLine("  (none — company defaults apply)");
        }
        else
        {
            var billing = client.BillingSettings;
            sb.AppendLine($"  Due date: {billing.DueDateCalculationType}, {billing.DueDays} days");
            sb.AppendLine($"  Payment method: {billing.DefaultPaymentMethod?.ToString() ?? "(default)"}");
            if (!string.IsNullOrWhiteSpace(billing.Notes))
                sb.AppendLine($"  Notes: {billing.Notes}");
        }

        return sb.ToString();
    }

    /// <summary>
    /// Writes a heading plus one indented line per item, or "(none)" when the list is empty.
    /// Shared by the three repeating sections so they cannot drift apart in formatting.
    /// </summary>
    private static void AppendSection<T>(
        StringBuilder sb,
        string heading,
        IReadOnlyList<T> items,
        Func<T, string> describe)
    {
        sb.AppendLine($"{heading}:");

        if (items.Count == 0)
        {
            sb.AppendLine("  (none)");
            return;
        }

        foreach (var item in items)
        {
            sb.AppendLine($"  - {describe(item)}");
        }
    }
}

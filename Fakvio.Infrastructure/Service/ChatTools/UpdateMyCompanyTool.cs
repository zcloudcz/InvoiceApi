using Fakvio.Application.Service;
using Fakvio.Contracts.Dto.Client;
using Microsoft.Extensions.Logging;

namespace Fakvio.Infrastructure.Service.ChatTools;

/// <summary>
/// Chat tool that changes the tenant's own company settings — the issuer (issue #220).
///
/// Typical usage:
///   "Změň název naší firmy na Fakvio s.r.o."
///   "Od ledna jsme plátci DPH."
///   "Přestěhovali jsme se na Wenceslas Square 1, Praha 1, 11000."
///
/// Data-changing, therefore <see cref="IConfirmableChatTool"/>: the first call only returns a
/// preview of what would change, and nothing is written until the user approves and the model
/// repeats the call with <c>confirm: true</c>. See DEVGUIDE §4.7.
///
/// Bank accounts are deliberately NOT part of this tool — they are a 1:N collection with their
/// own add / update / delete tools, so that "change the account number" cannot silently mean
/// "replace all our accounts with this one".
/// </summary>
public class UpdateMyCompanyTool : IConfirmableChatTool
{
    /// <summary>
    /// Address parameters, listed once. Sending any of them rewrites the primary address, so
    /// both the preview and the execution have to agree on exactly which names count as "the
    /// user is changing the address".
    /// </summary>
    private static readonly string[] AddressParameterNames = ["street", "city", "postal_code", "country"];

    private readonly IClientService _clientService;
    private readonly ILogger<UpdateMyCompanyTool> _logger;

    public UpdateMyCompanyTool(IClientService clientService, ILogger<UpdateMyCompanyTool> logger)
    {
        _clientService = clientService;
        _logger = logger;
    }

    public string ToolName => "update_my_company";

    public string Description =>
        "Change the settings of the user's own company (the issuer): name, trading name, DIČ, " +
        "VAT payer status, document language and the primary address. Send only the fields that " +
        "should change — everything else is left as it is. IČO cannot be changed. " +
        "Use add_bank_account / update_bank_account / delete_bank_account for bank accounts.";

    /// <summary>
    /// Parameter schema — static because it never changes per instance.
    /// Everything is optional: the tool changes exactly the fields the model sends, and it
    /// refuses the call when that set is empty (a rule the schema cannot express).
    /// </summary>
    private static readonly ChatToolParameter[] Schema =
    [
        new()
        {
            Name = "company_name",
            Type = ChatToolParameterType.String,
            Description = "Official company name as it appears on invoices"
        },
        new()
        {
            Name = "trading_name",
            Type = ChatToolParameterType.String,
            Description = "Trading name, when it differs from the official company name"
        },
        new()
        {
            Name = "tax_number",
            Type = ChatToolParameterType.String,
            Description = "VAT identification number (DIČ), e.g. CZ12345678"
        },
        new()
        {
            Name = "is_vat_payer",
            Type = ChatToolParameterType.Boolean,
            Description = "True when the company is registered for VAT (plátce DPH)"
        },
        new()
        {
            Name = "language",
            Type = ChatToolParameterType.String,
            Description = "Language of generated documents (invoices, e-mails)",
            AllowedValues = ["cs", "en"]
        },
        new()
        {
            Name = "street",
            Type = ChatToolParameterType.String,
            Description = "Street and number of the company's primary address"
        },
        new()
        {
            Name = "city",
            Type = ChatToolParameterType.String,
            Description = "City of the company's primary address"
        },
        new()
        {
            Name = "postal_code",
            Type = ChatToolParameterType.String,
            Description = "Postal code (PSČ) of the company's primary address"
        },
        new()
        {
            Name = "country",
            Type = ChatToolParameterType.String,
            Description = "Country of the company's primary address"
        }
    ];

    public IReadOnlyList<ChatToolParameter> Parameters => Schema;

    /// <summary>
    /// Describes what would change, field by field, without touching anything.
    /// </summary>
    public async Task<ChatToolResult> BuildPreviewAsync(
        Dictionary<string, string> parameters,
        CancellationToken ct = default)
    {
        var issuer = await _clientService.GetIssuerAsync(ct);
        if (issuer is null)
            return ChatToolResult.Failure(IssuerChatToolSupport.NoIssuerConfigured);

        var (_, changes) = BuildUpdate(issuer, parameters);
        if (changes.Count == 0)
            return ChatToolResult.Failure(NothingToChange);

        return ChatToolResult.Success(
            $"The company profile of '{issuer.CompanyName}' would change:\n" +
            string.Join("\n", changes.Select(change => $"  - {change}")));
    }

    /// <summary>
    /// Applies the change. The issuer is re-read here rather than carried over from the
    /// preview: the two calls are not paired, so the stored data may have moved on since
    /// (see <see cref="IConfirmableChatTool"/>).
    /// </summary>
    public async Task<ChatToolResult> ExecuteAsync(
        Dictionary<string, string> parameters,
        CancellationToken ct = default)
    {
        _logger.LogInformation("UpdateMyCompanyTool executing with parameters: {Params}",
            string.Join(", ", parameters.Keys));

        var issuer = await _clientService.GetIssuerAsync(ct);
        if (issuer is null)
            return ChatToolResult.Failure(IssuerChatToolSupport.NoIssuerConfigured);

        var (updateDto, changes) = BuildUpdate(issuer, parameters);
        if (changes.Count == 0)
            return ChatToolResult.Failure(NothingToChange);

        var updated = await _clientService.UpdateClientAsync(issuer.Id, updateDto, ct);
        if (updated is null)
            return ChatToolResult.Failure(
                $"The company profile (ID {issuer.Id}) could not be updated because it no longer exists.");

        _logger.LogInformation("Company profile {ClientId} updated: {Changes}",
            issuer.Id, string.Join("; ", changes));

        return ChatToolResult.Success(
            $"Company profile updated.\n{IssuerChatToolSupport.FormatCompany(updated)}");
    }

    /// <summary>Message used when the model calls the tool without naming a single field.</summary>
    private const string NothingToChange =
        "No change was requested. Send at least one field to change (for example company_name, " +
        "is_vat_payer or street).";

    /// <summary>
    /// Translates the supplied parameters into an <see cref="UpdateClientDto"/> plus a
    /// human-readable list of what changes. One method for both the preview and the write, so
    /// the preview can never promise something different from what is then saved.
    ///
    /// A parameter that repeats the stored value is not counted as a change — otherwise the
    /// preview would claim an edit where there is none.
    /// </summary>
    private static (UpdateClientDto Dto, List<string> Changes) BuildUpdate(
        ClientDto issuer,
        Dictionary<string, string> parameters)
    {
        var dto = new UpdateClientDto();
        var changes = new List<string>();

        var companyName = IssuerChatToolSupport.OptionalText(parameters, "company_name");
        if (companyName is not null && companyName != issuer.CompanyName)
        {
            dto.CompanyName = companyName;
            changes.Add($"name: '{issuer.CompanyName}' → '{companyName}'");
        }

        var tradingName = IssuerChatToolSupport.OptionalText(parameters, "trading_name");
        if (tradingName is not null && tradingName != issuer.TradingName)
        {
            dto.TradingName = tradingName;
            changes.Add($"trading name: '{issuer.TradingName ?? "(not set)"}' → '{tradingName}'");
        }

        var taxNumber = IssuerChatToolSupport.OptionalText(parameters, "tax_number");
        if (taxNumber is not null && taxNumber != issuer.TaxNumber)
        {
            dto.TaxNumber = taxNumber;
            changes.Add($"DIČ: '{issuer.TaxNumber ?? "(not set)"}' → '{taxNumber}'");
        }

        var isVatPayer = IssuerChatToolSupport.OptionalFlag(parameters, "is_vat_payer");
        if (isVatPayer is not null && isVatPayer != issuer.IsVatPayer)
        {
            dto.IsVatPayer = isVatPayer;
            changes.Add($"VAT payer: {(issuer.IsVatPayer ? "yes" : "no")} → {(isVatPayer.Value ? "yes" : "no")}");
        }

        var language = IssuerChatToolSupport.OptionalText(parameters, "language");
        if (language is not null && !string.Equals(language, issuer.Language, StringComparison.OrdinalIgnoreCase))
        {
            dto.Language = language.ToLowerInvariant();
            changes.Add($"document language: '{issuer.Language}' → '{dto.Language}'");
        }

        ApplyAddress(issuer, parameters, dto, changes);

        return (dto, changes);
    }

    /// <summary>
    /// Rewrites the primary address when at least one address field was supplied.
    ///
    /// The whole address collection has to be sent, because <c>UpdateClientAsync</c> replaces
    /// it wholesale (the same call the My Company page makes). Secondary addresses are carried
    /// over unchanged; only the primary one — or the first one, when none is flagged primary —
    /// takes the new values. With no address on file at all, one is created.
    /// </summary>
    private static void ApplyAddress(
        ClientDto issuer,
        Dictionary<string, string> parameters,
        UpdateClientDto dto,
        List<string> changes)
    {
        var street = IssuerChatToolSupport.OptionalText(parameters, "street");
        var city = IssuerChatToolSupport.OptionalText(parameters, "city");
        var postalCode = IssuerChatToolSupport.OptionalText(parameters, "postal_code");
        var country = IssuerChatToolSupport.OptionalText(parameters, "country");

        if (street is null && city is null && postalCode is null && country is null)
            return;

        var current = issuer.Address.FirstOrDefault(address => address.IsPrimary)
                      ?? issuer.Address.FirstOrDefault();

        var replacement = new UpdateAddressDto
        {
            // Null AddressType means "Primary" in ClientService — right for a brand new address,
            // and for an existing one we keep whatever type it already had.
            AddressType = current?.AddressType,
            Street = street ?? current?.Street ?? string.Empty,
            City = city ?? current?.City ?? string.Empty,
            PostalCode = postalCode ?? current?.PostalCode ?? string.Empty,
            Country = country ?? current?.Country ?? string.Empty,
            AddressLine2 = current?.AddressLine2,
            IsPrimary = true
        };

        var before = current is null
            ? "(not set)"
            : $"{current.Street}, {current.PostalCode} {current.City}, {current.Country}";
        var after =
            $"{replacement.Street}, {replacement.PostalCode} {replacement.City}, {replacement.Country}";

        if (before == after)
            return;

        // Every other address is carried over untouched — the collection is replaced as a whole,
        // so anything left out here would be deleted.
        dto.Address = issuer.Address
            .Where(address => !ReferenceEquals(address, current))
            .Select(address => new UpdateAddressDto
            {
                AddressType = address.AddressType,
                Street = address.Street,
                City = address.City,
                PostalCode = address.PostalCode,
                Country = address.Country,
                AddressLine2 = address.AddressLine2,
                IsPrimary = false
            })
            .Prepend(replacement)
            .ToList();

        changes.Add($"primary address: '{before}' → '{after}'");
    }
}

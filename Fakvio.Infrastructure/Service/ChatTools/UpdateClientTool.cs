using System.Text;
using Fakvio.Application.Service;
using Fakvio.Contracts.Dto.Client;
using Microsoft.Extensions.Logging;

namespace Fakvio.Infrastructure.Service.ChatTools;

/// <summary>
/// Chat tool that changes the identity fields of an existing client — behind the confirm gate
/// from issue #212, because it overwrites data the user cannot get back from the chat.
///
/// Typical usage:
///   "Přejmenuj klienta Alfa na Alfa Trade s.r.o."  → preview → user agrees → the model
///                                                     repeats the call with confirm: true
///
/// Deliberately limited to the scalar fields, exactly like the MCP <c>UpdateClient</c> tool:
/// addresses, contacts and bank accounts are REPLACE-ALL collections in
/// <see cref="UpdateClientDto"/>, so a model that sends one address wipes the other three.
/// Editing those stays in the UI until someone asks for it.
///
/// Junior note on what the gate is NOT: an authorization boundary. Editing a client is
/// something the user can already do in the UI — the gate only stops the assistant from doing
/// it silently. The <c>confirm</c> parameter is added by the executor, never declared here
/// (see <see cref="ChatToolConfirmation"/> and DEVGUIDE §4.7).
/// </summary>
public class UpdateClientTool : IConfirmableChatTool
{
    private readonly IClientService _clientService;
    private readonly ILogger<UpdateClientTool> _logger;

    public UpdateClientTool(IClientService clientService, ILogger<UpdateClientTool> logger)
    {
        _clientService = clientService;
        _logger = logger;
    }

    public string ToolName => "update_client";

    public string Description =>
        "Update an existing client: company name, trading name, DIČ, VAT payer flag, active " +
        "flag, or refresh the data from the ARES registry. Identify the client by id, " +
        "registration_number (IČO) or name. Only the fields you send are changed. " +
        "Addresses, contacts and bank accounts cannot be edited here.";

    /// <summary>
    /// The fields this tool may change, on top of the shared identity parameters.
    /// Kept as its own array so the "did the model ask for any change at all?" check
    /// below cannot fall out of sync with the schema.
    /// </summary>
    private static readonly ChatToolParameter[] ChangeableFields =
    [
        new()
        {
            Name = "company_name",
            Type = ChatToolParameterType.String,
            Description = "New official company name"
        },
        new()
        {
            Name = "trading_name",
            Type = ChatToolParameterType.String,
            Description = "New trading name (used when it differs from the official name)"
        },
        new()
        {
            Name = "tax_number",
            Type = ChatToolParameterType.String,
            Description = "New tax identification number (DIČ), e.g. 'CZ12345678'"
        },
        new()
        {
            Name = "is_vat_payer",
            Type = ChatToolParameterType.Boolean,
            Description = "Whether the company is a VAT payer"
        },
        new()
        {
            Name = "is_active",
            Type = ChatToolParameterType.Boolean,
            Description = "Whether the client is active. False hides the client the same way " +
                          "delete_client does; true restores a deleted client."
        },
        new()
        {
            Name = "refresh_from_ares",
            Type = ChatToolParameterType.Boolean,
            Description = "True to overwrite name, DIČ and VAT status with current ARES data. " +
                          "Requires the client to have a registration number (IČO)."
        }
    ];

    private static readonly ChatToolParameter[] Schema =
        [.. ClientLookup.IdentityParameters, .. ChangeableFields];

    public IReadOnlyList<ChatToolParameter> Parameters => Schema;

    /// <summary>
    /// Spells out every field that would change, old value → new value, so the user confirms
    /// a concrete edit and not just the word "update". Reads only — the executor appends the
    /// "confirm?" wording itself.
    /// </summary>
    public async Task<ChatToolResult> BuildPreviewAsync(
        Dictionary<string, string> parameters,
        CancellationToken ct = default)
    {
        var (client, error) = await ResolveAndValidateAsync(parameters, ct);
        if (error is not null)
            return error;

        return ChatToolResult.Success(
            $"This would change the client:\n{ClientLookup.Describe(client!)}\n\n" +
            DescribeChanges(client!, parameters));
    }

    public async Task<ChatToolResult> ExecuteAsync(
        Dictionary<string, string> parameters,
        CancellationToken ct = default)
    {
        // Resolved and validated again, deliberately: preview and execution are two independent
        // calls with the user's turns in between (see IConfirmableChatTool), so nothing from the
        // preview is reused — the client may have been renamed or deleted meanwhile.
        var (client, error) = await ResolveAndValidateAsync(parameters, ct);
        if (error is not null)
            return error;

        var updateDto = BuildUpdateDto(parameters);

        _logger.LogInformation("UpdateClientTool: updating client {ClientId}", client!.Id);

        var updated = await _clientService.UpdateClientAsync(client.Id, updateDto, ct);

        // Null means the row vanished between the lookup and the update — rare, but the model
        // must not report a change that did not happen.
        return updated is null
            ? ChatToolResult.Failure($"Client with ID {client.Id} no longer exists — nothing was changed.")
            : ChatToolResult.Success($"Client updated:\n{ClientLookup.Describe(updated)}");
    }

    /// <summary>
    /// Finds the client and checks the two rules the JSON schema cannot express:
    /// at least one field to change, and ARES refresh only for a client that has an IČO.
    /// Returns the client OR a ready-to-return failure — never both.
    /// </summary>
    private async Task<(ClientDto? Client, ChatToolResult? Error)> ResolveAndValidateAsync(
        Dictionary<string, string> parameters,
        CancellationToken ct)
    {
        var resolution = await ClientLookup.ResolveAsync(_clientService, parameters, ct);
        if (resolution.Error is not null)
            return (null, resolution.Error);

        var client = resolution.Client!;

        if (!ChangeableFields.Any(field => parameters.ContainsKey(field.Name)))
        {
            return (null, ChatToolResult.Failure(
                "Nothing to change — send at least one of: " +
                $"{string.Join(", ", ChangeableFields.Select(field => field.Name))}."));
        }

        // A blank name would be written straight through: the [StringLength(MinimumLength = 1)]
        // attribute on UpdateClientDto is only enforced by API model binding, and a chat tool
        // calls the service directly. Every invoice PDF prints this field.
        if (parameters.TryGetValue("company_name", out var newName) && string.IsNullOrWhiteSpace(newName))
        {
            return (null, ChatToolResult.Failure(
                "company_name cannot be empty — every client must keep a name."));
        }

        // Fail fast rather than let ClientService silently skip the refresh: the user asked for
        // ARES data and would otherwise be told the update succeeded with nothing refreshed.
        if (WantsAresRefresh(parameters) && string.IsNullOrWhiteSpace(client.RegistrationNumber))
        {
            return (null, ChatToolResult.Failure(
                $"Client '{client.CompanyName}' has no registration number (IČO), " +
                "so its data cannot be refreshed from ARES."));
        }

        return (client, null);
    }

    /// <summary>Maps the tool parameters onto the update DTO. Absent parameter = "don't change".</summary>
    private static UpdateClientDto BuildUpdateDto(Dictionary<string, string> parameters)
        => new()
        {
            CompanyName = ReadText(parameters, "company_name"),
            TradingName = ReadText(parameters, "trading_name"),
            TaxNumber = ReadText(parameters, "tax_number"),
            IsVatPayer = ReadBool(parameters, "is_vat_payer"),
            IsActive = ReadBool(parameters, "is_active"),
            RefreshFromAres = WantsAresRefresh(parameters)
        };

    /// <summary>
    /// Lists the requested changes as "field: old → new", skipping fields the model did not send.
    /// The ARES refresh gets a sentence instead of a value pair — what ARES will return is not
    /// known until the write runs, and promising a value we have not fetched would be a lie.
    /// </summary>
    private static string DescribeChanges(ClientDto client, Dictionary<string, string> parameters)
    {
        var sb = new StringBuilder("Requested changes:");
        sb.AppendLine();

        AppendChange(sb, parameters, "company_name", "Name", client.CompanyName);
        AppendChange(sb, parameters, "trading_name", "Trading name", client.TradingName ?? "(none)");
        AppendChange(sb, parameters, "tax_number", "DIČ", client.TaxNumber ?? "(none)");
        AppendChange(sb, parameters, "is_vat_payer", "VAT payer", ClientLookup.YesNo(client.IsVatPayer));
        AppendChange(sb, parameters, "is_active", "Active", ClientLookup.YesNo(client.IsActive));

        if (WantsAresRefresh(parameters))
        {
            sb.AppendLine(
                $"  - Name, DIČ and VAT status will be overwritten with current ARES data " +
                $"for IČO {client.RegistrationNumber}.");
        }

        return sb.ToString();
    }

    private static void AppendChange(
        StringBuilder sb,
        Dictionary<string, string> parameters,
        string parameterName,
        string label,
        string currentValue)
    {
        if (parameters.TryGetValue(parameterName, out var newValue))
            sb.AppendLine($"  - {label}: {currentValue} → {newValue}");
    }

    /// <summary>
    /// True only for an explicit <c>true</c>. <c>refresh_from_ares: false</c> means "leave it",
    /// which is also what the DTO's default does.
    /// </summary>
    private static bool WantsAresRefresh(Dictionary<string, string> parameters)
        => ReadBool(parameters, "refresh_from_ares") == true;

    // Types are validated centrally by ChatToolExecutor before the tool runs, so these only
    // translate "not supplied" into null — they are not a second validation.

    private static string? ReadText(Dictionary<string, string> parameters, string key)
        => parameters.TryGetValue(key, out var raw) ? raw.Trim() : null;

    private static bool? ReadBool(Dictionary<string, string> parameters, string key)
        => parameters.TryGetValue(key, out var raw) && bool.TryParse(raw, out var value)
            ? value
            : null;
}

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
/// <c>Language</c> (document language, "cs"/"en") is intentionally NOT exposed here (see #306):
/// it is a document-rendering preference, not an identity field a user corrects mid-conversation
/// like the ones above, and no chat request for it has come up. Add it the same way as
/// <c>company_name</c> if that changes — the MCP <c>UpdateClient</c> tool already supports it.
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
    /// Kept as its own array so the "nothing to change" message can list them without
    /// repeating the schema by hand.
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
            Description = "New trading name (used when it differs from the official name). " +
                          "Cannot be cleared from chat — send a value or omit the field."
        },
        new()
        {
            Name = "tax_number",
            Type = ChatToolParameterType.String,
            Description = "New tax identification number (DIČ), e.g. 'CZ12345678'. " +
                          "Cannot be cleared from chat — send a value or omit the field."
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
        var (client, update, error) = await ResolveAndValidateAsync(parameters, ct);
        if (error is not null)
            return error;

        return ChatToolResult.Success(
            $"This would change the client:\n{ClientLookup.Describe(client!)}\n\n" +
            DescribeChanges(client!, update!));
    }

    public async Task<ChatToolResult> ExecuteAsync(
        Dictionary<string, string> parameters,
        CancellationToken ct = default)
    {
        // Resolved and validated again, deliberately: preview and execution are two independent
        // calls with the user's turns in between (see IConfirmableChatTool), so nothing from the
        // preview is reused — the client may have been renamed or deleted meanwhile.
        var (client, update, error) = await ResolveAndValidateAsync(parameters, ct);
        if (error is not null)
            return error;

        _logger.LogInformation("UpdateClientTool: updating client {ClientId}", client!.Id);

        var updated = await _clientService.UpdateClientAsync(client.Id, update!, ct);

        // Null means the row vanished between the lookup and the update — rare, but the model
        // must not report a change that did not happen.
        if (updated is null)
            return ChatToolResult.Failure($"Client with ID {client.Id} no longer exists — nothing was changed.");

        // The other half of the ARES door. ClientService only overwrites the fields when the
        // registry answers (aresInfo.IsSuccessful); when it is down or does not know the IČO it
        // saves the rest and returns a DTO anyway, so "not null" is no proof the refresh ran.
        // The one observable signal is LastAresFetchDate — the service stamps it ONLY after a
        // successful fetch, so an unchanged timestamp means we got nothing from ARES.
        if (update!.RefreshFromAres && updated.LastAresFetchDate == client.LastAresFetchDate)
        {
            return ChatToolResult.Failure(
                $"ARES returned no usable data for IČO {client.RegistrationNumber} (registry " +
                "unavailable or IČO unknown), so the client was NOT refreshed from ARES. " +
                (WritesExplicitFields(update)
                    ? $"The fields you sent explicitly were saved:\n{ClientLookup.Describe(updated)}"
                    : "Nothing else was changed."));
        }

        return ChatToolResult.Success($"Client updated:\n{ClientLookup.Describe(updated)}");
    }

    /// <summary>
    /// Finds the client, builds the update, and checks the two rules the JSON schema cannot
    /// express: at least one field to change, and ARES refresh only for a client that has an
    /// IČO. Returns the client plus its update OR a ready-to-return failure — never both.
    /// </summary>
    private async Task<(ClientDto? Client, UpdateClientDto? Update, ChatToolResult? Error)> ResolveAndValidateAsync(
        Dictionary<string, string> parameters,
        CancellationToken ct)
    {
        var resolution = await ClientLookup.ResolveAsync(_clientService, parameters, ct);
        if (resolution.Error is not null)
            return (null, null, resolution.Error);

        var client = resolution.Client!;

        // A blank name would be written straight through: the [StringLength(MinimumLength = 1)]
        // attribute on UpdateClientDto is only enforced by API model binding, and a chat tool
        // calls the service directly. Every invoice PDF prints this field.
        if (parameters.TryGetValue("company_name", out var newName) && string.IsNullOrWhiteSpace(newName))
        {
            return (null, null, ChatToolResult.Failure(
                "company_name cannot be empty — every client must keep a name."));
        }

        var update = BuildUpdateDto(parameters);

        // "Did the model ask for anything?" is answered by the DTO, not by the keys in the
        // dictionary. ChatToolExecutor treats a blank value as "not supplied" but leaves the key
        // where it is, and 'refresh_from_ares: false' means "leave it alone" — so asking the
        // keys would accept 'is_vat_payer: ""' as a change, write an empty update and report
        // "Client updated" for something that changed nothing.
        if (!RequestsAnyChange(update))
        {
            return (null, null, ChatToolResult.Failure(
                "Nothing to change — send at least one of: " +
                $"{string.Join(", ", ChangeableFields.Select(field => field.Name))}."));
        }

        // Fail fast rather than let ClientService silently skip the refresh: the user asked for
        // ARES data and would otherwise be told the update succeeded with nothing refreshed.
        if (update.RefreshFromAres && string.IsNullOrWhiteSpace(client.RegistrationNumber))
        {
            return (null, null, ChatToolResult.Failure(
                $"Client '{client.CompanyName}' has no registration number (IČO), " +
                "so its data cannot be refreshed from ARES."));
        }

        return (client, update, null);
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
            // Only an explicit 'true' refreshes. 'false' means "leave it", which is also what
            // the DTO's default does.
            RefreshFromAres = ReadBool(parameters, "refresh_from_ares") == true
        };

    /// <summary>
    /// True when the update carries at least one field the user spelled out. Kept separate from
    /// <see cref="RequestsAnyChange"/> because the ARES report has to tell "your fields were
    /// saved, only the refresh failed" apart from "nothing happened at all".
    /// </summary>
    private static bool WritesExplicitFields(UpdateClientDto update)
        => update.CompanyName is not null
           || update.TradingName is not null
           || update.TaxNumber is not null
           || update.IsVatPayer.HasValue
           || update.IsActive.HasValue;

    /// <summary>True when the update would do anything at all — explicit fields or ARES refresh.</summary>
    private static bool RequestsAnyChange(UpdateClientDto update)
        => WritesExplicitFields(update) || update.RefreshFromAres;

    /// <summary>
    /// Lists the requested changes as "field: old → new", skipping fields the model did not send.
    /// Reads the update DTO rather than the raw parameters, so the preview shows exactly what the
    /// write would do — a value the tool decided to ignore can never appear here as a change.
    ///
    /// The ARES refresh gets a sentence instead of a value pair — what ARES will return is not
    /// known until the write runs, and promising a value we have not fetched would be a lie.
    /// </summary>
    private static string DescribeChanges(ClientDto client, UpdateClientDto update)
    {
        var sb = new StringBuilder("Requested changes:");
        sb.AppendLine();

        AppendChange(sb, "Name", client.CompanyName, update.CompanyName);
        AppendChange(sb, "Trading name", client.TradingName ?? "(none)", update.TradingName);
        AppendChange(sb, "DIČ", client.TaxNumber ?? "(none)", update.TaxNumber);
        AppendChange(sb, "VAT payer", ClientLookup.YesNo(client.IsVatPayer), YesNoOrNull(update.IsVatPayer));
        AppendChange(sb, "Active", ClientLookup.YesNo(client.IsActive), YesNoOrNull(update.IsActive));

        if (update.RefreshFromAres)
        {
            sb.AppendLine(
                $"  - Name, DIČ and VAT status will be overwritten with current ARES data " +
                $"for IČO {client.RegistrationNumber}.");

            // ClientService applies the ARES data first and the explicit fields after it, so a
            // field listed above wins over what the registry says. Without this line the two
            // bullets would contradict each other.
            if (WritesExplicitFields(update))
                sb.AppendLine("    The values listed above are written after the ARES data and take precedence.");
        }

        return sb.ToString();
    }

    private static void AppendChange(StringBuilder sb, string label, string currentValue, string? newValue)
    {
        if (newValue is not null)
            sb.AppendLine($"  - {label}: {currentValue} → {newValue}");
    }

    /// <summary>Renders a nullable flag the same way the rest of the output does — or nothing.</summary>
    private static string? YesNoOrNull(bool? value) => value.HasValue ? ClientLookup.YesNo(value.Value) : null;

    // Types are validated centrally by ChatToolExecutor before the tool runs, so these only
    // translate "not supplied" into null — they are not a second validation.

    /// <summary>
    /// Blank counts as "not supplied", the same way <see cref="ChatToolExecutor"/> already treats
    /// it — models send empty strings for parameters they have no value for. The side effect is
    /// that a field cannot be cleared from chat; that is deliberate, since "" would be stored as
    /// an empty string and then read back as blank while a never-filled field reads "(none)".
    /// </summary>
    private static string? ReadText(Dictionary<string, string> parameters, string key)
        => parameters.TryGetValue(key, out var raw) && !string.IsNullOrWhiteSpace(raw)
            ? raw.Trim()
            : null;

    private static bool? ReadBool(Dictionary<string, string> parameters, string key)
        => parameters.TryGetValue(key, out var raw) && bool.TryParse(raw, out var value)
            ? value
            : null;
}

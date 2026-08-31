using Fakvio.Application.Service;
using Microsoft.Extensions.Logging;

namespace Fakvio.Infrastructure.Service.ChatTools;

/// <summary>
/// Chat tool that reads the reminder (dunning) settings — issue #227.
///
/// Typical usage:
///   "Jaké máme nastavení upomínek?"
///   "Posíláme upomínky automaticky?"
///   "Jaké nastavení upomínek platí pro klienta ABC?"
///
/// Read-only, so no confirmation step. Settings resolve in two tiers: a client-level override
/// wins over the company-wide default. Without a client the tool reports the company default;
/// with one it reports what would actually be applied to that client's invoices — and says
/// which of the two it is, because the numbers alone do not reveal it.
/// </summary>
public class GetReminderSettingsTool : IChatTool
{
    private readonly IReminderService _reminderService;
    private readonly IClientService _clientService;
    private readonly ILogger<GetReminderSettingsTool> _logger;

    public GetReminderSettingsTool(
        IReminderService reminderService,
        IClientService clientService,
        ILogger<GetReminderSettingsTool> logger)
    {
        _reminderService = reminderService;
        _clientService = clientService;
        _logger = logger;
    }

    public string ToolName => "get_reminder_settings";

    public string Description =>
        "Read the payment reminder (dunning) settings: whether reminders are enabled, the grace " +
        "period, the number of escalation levels with their fees, and whether e-mails are sent " +
        "automatically. Without a client it returns the company-wide default; with a client it " +
        "returns the settings that actually apply to that client. Read-only.";

    /// <summary>
    /// Parameter schema — the standard client identifiers, all optional here.
    ///
    /// Reused from <see cref="ClientLookup"/> on purpose: "which client?" must be asked with
    /// the same three parameters in every tool, otherwise the model has to learn a second
    /// spelling for the same question.
    /// </summary>
    private static readonly ChatToolParameter[] Schema = [.. ClientLookup.IdentityParameters];

    public IReadOnlyList<ChatToolParameter> Parameters => Schema;

    public async Task<ChatToolResult> ExecuteAsync(
        Dictionary<string, string> parameters,
        CancellationToken ct = default)
    {
        _logger.LogInformation("GetReminderSettingsTool executing with parameters: {Params}",
            string.Join(", ", parameters.Keys));

        long? clientId = null;

        // A client is optional here, unlike in the get / update / delete client tools — so the
        // "at least one identifier" rule of ClientLookup.ResolveAsync only applies once the
        // model has actually named one.
        if (NamesAClient(parameters))
        {
            var resolution = await ClientLookup.ResolveAsync(_clientService, parameters, ct);
            if (resolution.Error is not null)
                return resolution.Error;

            clientId = resolution.Client!.Id;
        }

        // GetEffectiveSettingsAsync is the read-only path: it returns null when nothing is
        // stored. GetCompanySettingsAsync would CREATE a default record, which a read tool
        // must never do.
        var settings = await _reminderService.GetEffectiveSettingsAsync(clientId, ct);
        if (settings is null)
            return ChatToolResult.Success(ReminderChatToolSupport.NoSettingsStored);

        // The 2-tier fallback is silent in the DTO: asking for client 5 can return the company
        // default, and that difference decides whether changing the default affects this client.
        var scopeNote = clientId is not null && settings.ClientId is null
            ? "\nThis client has no own override — the company-wide default below applies to it.\n"
            : string.Empty;

        return ChatToolResult.Success(scopeNote + ReminderChatToolSupport.FormatSettings(settings));
    }

    /// <summary>
    /// True when the model supplied any of the client identifiers with a non-blank value.
    /// Blank counts as "not sent", the same way <see cref="ClientLookup"/> reads them.
    /// </summary>
    private static bool NamesAClient(Dictionary<string, string> parameters)
        => ClientLookup.IdentityParameters.Any(parameter =>
            parameters.TryGetValue(parameter.Name, out var raw) && !string.IsNullOrWhiteSpace(raw));
}

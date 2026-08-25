using System.Globalization;
using Fakvio.Application.Service;
using Fakvio.Contracts.Dto.Reminder;
using Microsoft.Extensions.Logging;

namespace Fakvio.Infrastructure.Service.ChatTools;

/// <summary>
/// Chat tool that changes the company-wide reminder (dunning) settings — issue #227.
///
/// Typical usage:
///   "Vypni upomínky."
///   "Upomínej až 14 dní po splatnosti."
///   "Upomínky ať se neposílají samy, chci je nejdřív vidět."
///
/// Data-changing, therefore <see cref="IConfirmableChatTool"/>: the first call only returns a
/// preview of what would change, and nothing is written until the user approves and the model
/// repeats the call with <c>confirm: true</c>. See DEVGUIDE §4.7.
///
/// Two deliberate limits:
/// <list type="bullet">
/// <item>Only the <b>company-wide default</b> is editable. Per-client overrides are a 2-tier
/// mechanism whose "create an override" and "edit the existing one" cases look identical from
/// the outside, so a mistaken client would silently write a brand new override instead of the
/// setting the user meant. They stay on the Reminders → Settings page.</item>
/// <item>The escalation <b>levels are not editable</b> here — they are a nested collection
/// (days, fee, subject, two template IDs per level) that this tool carries over untouched.
/// Only the scalar settings around them change.</item>
/// </list>
/// </summary>
public class UpdateReminderSettingsTool : IConfirmableChatTool
{
    /// <summary>Escalation levels supported by the dunning job (see <c>ReminderSettings</c>).</summary>
    private const int MinEscalationLevel = 1;

    private const int MaxEscalationLevel = 5;

    /// <summary>
    /// Upper bound for the grace period. Not a domain rule — a sanity limit, so a model that
    /// turns "a year or so" into 3650 gets a clear error instead of switching dunning off in
    /// practice while the settings page still claims it is on.
    /// </summary>
    private const int MaxGracePeriodDays = 365;

    private readonly IReminderService _reminderService;
    private readonly ILogger<UpdateReminderSettingsTool> _logger;

    public UpdateReminderSettingsTool(
        IReminderService reminderService,
        ILogger<UpdateReminderSettingsTool> logger)
    {
        _reminderService = reminderService;
        _logger = logger;
    }

    public string ToolName => "update_reminder_settings";

    public string Description =>
        "Change the company-wide payment reminder (dunning) settings: switch reminders on or off, " +
        "the grace period after the due date, how many escalation levels are used, whether late " +
        "payment interest is added, whether the invoice PDF is attached, and whether reminder " +
        "e-mails are sent automatically. Send only the settings that should change. The escalation " +
        "levels themselves and per-client overrides are edited on the Reminders settings page.";

    /// <summary>
    /// Parameter schema — static because it never changes per instance.
    /// Everything is optional: the tool changes exactly the settings the model sends, and it
    /// refuses the call when that set is empty (a rule the schema cannot express).
    /// </summary>
    private static readonly ChatToolParameter[] Schema =
    [
        new()
        {
            Name = "is_enabled",
            Type = ChatToolParameterType.Boolean,
            Description = "Master switch — false stops all reminders from being generated"
        },
        new()
        {
            Name = "grace_period_days",
            Type = ChatToolParameterType.Integer,
            Description = $"Days after the due date before the first reminder (0-{MaxGracePeriodDays})"
        },
        new()
        {
            Name = "max_reminder_level",
            Type = ChatToolParameterType.Integer,
            Description = $"Highest escalation level that may be reached ({MinEscalationLevel}-{MaxEscalationLevel})"
        },
        new()
        {
            Name = "include_interest",
            Type = ChatToolParameterType.Boolean,
            Description = "True to add statutory late payment interest to reminders"
        },
        new()
        {
            Name = "attach_invoice_pdf",
            Type = ChatToolParameterType.Boolean,
            Description = "True to attach the invoice PDF to reminder e-mails"
        },
        new()
        {
            Name = "auto_send_email",
            Type = ChatToolParameterType.Boolean,
            Description = "True to e-mail reminders automatically, false to create them as drafts for manual review"
        }
    ];

    public IReadOnlyList<ChatToolParameter> Parameters => Schema;

    /// <summary>
    /// Describes what would change, setting by setting, without touching anything.
    ///
    /// It reads through <c>GetEffectiveSettingsAsync(null)</c> rather than
    /// <c>GetCompanySettingsAsync</c>, because the latter creates a default record when none
    /// exists — a write, which a preview must never do.
    /// </summary>
    public async Task<ChatToolResult> BuildPreviewAsync(
        Dictionary<string, string> parameters,
        CancellationToken ct = default)
    {
        if (Validate(parameters) is { } validationError)
            return ChatToolResult.Failure(validationError);

        var current = await _reminderService.GetEffectiveSettingsAsync(null, ct);

        // Nothing stored yet: there is no "from" value to show, so the preview lists the
        // requested values and warns that the standard defaults get created underneath them.
        if (current is null)
        {
            var requested = DescribeRequested(parameters);
            return requested.Count == 0
                ? ChatToolResult.Failure(NothingToChange)
                : ChatToolResult.Success(
                    "No reminder settings are stored yet, so the standard defaults would be created " +
                    "first and then changed to:\n" +
                    string.Join("\n", requested.Select(change => $"  - {change}")));
        }

        var (_, changes) = BuildUpdate(current, parameters);
        return changes.Count == 0
            ? ChatToolResult.Failure(NothingToChange)
            : ChatToolResult.Success(
                "The company-wide reminder settings would change:\n" +
                string.Join("\n", changes.Select(change => $"  - {change}")));
    }

    /// <summary>
    /// Applies the change. The settings are re-read here rather than carried over from the
    /// preview: the two calls are not paired, so the stored data may have moved on since
    /// (see <see cref="IConfirmableChatTool"/>).
    /// </summary>
    public async Task<ChatToolResult> ExecuteAsync(
        Dictionary<string, string> parameters,
        CancellationToken ct = default)
    {
        _logger.LogInformation("UpdateReminderSettingsTool executing with parameters: {Params}",
            string.Join(", ", parameters.Keys));

        if (Validate(parameters) is { } validationError)
            return ChatToolResult.Failure(validationError);

        // Bail out before the read below, which creates the default record when the tenant has
        // none: a call that names no setting at all must not leave anything stored behind.
        if (DescribeRequested(parameters).Count == 0)
            return ChatToolResult.Failure(NothingToChange);

        // Whether anything was stored decides how an otherwise-empty diff has to read, so probe
        // through the non-creating getter first. Asking for a value that happens to equal the
        // default is not a no-op when nothing exists yet — the record still gets created, and
        // reporting NothingToChange after that would tell the user the opposite of what happened.
        var existed = await _reminderService.GetEffectiveSettingsAsync(null, ct) is not null;

        // This is the write path, so creating the default record when the tenant has none is
        // exactly right — the user asked for a setting, and there has to be something to hold it.
        var current = await _reminderService.GetCompanySettingsAsync(ct);

        var (dto, changes) = BuildUpdate(current, parameters);
        if (existed && changes.Count == 0)
            return ChatToolResult.Failure(NothingToChange);

        var updated = await _reminderService.UpsertSettingsAsync(dto, ct);

        _logger.LogInformation("Company reminder settings updated: {Changes}", string.Join("; ", changes));

        return ChatToolResult.Success(
            $"Reminder settings updated.\n{ReminderChatToolSupport.FormatSettings(updated)}");
    }

    /// <summary>Message used when the model calls the tool without naming a single setting.</summary>
    private const string NothingToChange =
        "No change was requested. Send at least one setting to change (for example is_enabled, " +
        "grace_period_days or auto_send_email).";

    /// <summary>
    /// Checks the two numeric ranges the JSON Schema cannot express. Returns null when the
    /// parameters are acceptable, otherwise the message the model gets back.
    ///
    /// Fail fast, and in both entry points: without it the service would happily store
    /// <c>max_reminder_level = 0</c>, which silently disables dunning while the settings page
    /// keeps claiming reminders are enabled.
    /// </summary>
    private static string? Validate(Dictionary<string, string> parameters)
    {
        var maxLevel = OptionalWholeNumber(parameters, "max_reminder_level");
        if (maxLevel is < MinEscalationLevel or > MaxEscalationLevel)
            return $"max_reminder_level must be between {MinEscalationLevel} and {MaxEscalationLevel} (got {maxLevel}).";

        var gracePeriod = OptionalWholeNumber(parameters, "grace_period_days");
        if (gracePeriod is < 0 or > MaxGracePeriodDays)
            return $"grace_period_days must be between 0 and {MaxGracePeriodDays} (got {gracePeriod}).";

        return null;
    }

    /// <summary>
    /// Translates the supplied parameters into an <see cref="UpdateReminderSettingsDto"/> plus a
    /// human-readable list of what changes. One method for both the preview and the write, so
    /// the preview can never promise something different from what is then saved.
    ///
    /// A parameter that repeats the stored value is not counted as a change — otherwise the
    /// preview would claim an edit where there is none.
    /// </summary>
    private static (UpdateReminderSettingsDto Dto, List<string> Changes) BuildUpdate(
        ReminderSettingsDto current,
        Dictionary<string, string> parameters)
    {
        // Start from what is stored: UpsertSettingsAsync overwrites every scalar and REPLACES
        // the whole level collection, so anything left out here would fall back to the DTO
        // defaults — and the escalation levels would be deleted outright.
        var dto = new UpdateReminderSettingsDto
        {
            ClientId = null,
            IsEnabled = current.IsEnabled,
            MaxReminderLevel = current.MaxReminderLevel,
            GracePeriodDays = current.GracePeriodDays,
            IncludeInterest = current.IncludeInterest,
            AttachInvoicePdf = current.AttachInvoicePdf,
            AutoSendEmail = current.AutoSendEmail,
            Levels = [.. current.Levels.Select(level => new UpdateReminderLevelDto
            {
                Level = level.Level,
                DaysAfterPrevious = level.DaysAfterPrevious,
                Subject = level.Subject,
                FixedFeeCzk = level.FixedFeeCzk,
                EmailTemplateId = level.EmailTemplateId,
                PdfTemplateId = level.PdfTemplateId
            })]
        };

        var changes = new List<string>();

        var isEnabled = OptionalFlag(parameters, "is_enabled");
        if (isEnabled is not null && isEnabled != current.IsEnabled)
        {
            dto.IsEnabled = isEnabled.Value;
            changes.Add($"reminders enabled: {ClientLookup.YesNo(current.IsEnabled)} → {ClientLookup.YesNo(isEnabled.Value)}");
        }

        var gracePeriod = OptionalWholeNumber(parameters, "grace_period_days");
        if (gracePeriod is not null && gracePeriod != current.GracePeriodDays)
        {
            dto.GracePeriodDays = gracePeriod.Value;
            changes.Add($"grace period: {current.GracePeriodDays} → {gracePeriod} days");
        }

        var maxLevel = OptionalWholeNumber(parameters, "max_reminder_level");
        if (maxLevel is not null && maxLevel != current.MaxReminderLevel)
        {
            dto.MaxReminderLevel = maxLevel.Value;
            changes.Add($"maximum escalation level: {current.MaxReminderLevel} → {maxLevel}");
        }

        var includeInterest = OptionalFlag(parameters, "include_interest");
        if (includeInterest is not null && includeInterest != current.IncludeInterest)
        {
            dto.IncludeInterest = includeInterest.Value;
            changes.Add($"late payment interest: {ClientLookup.YesNo(current.IncludeInterest)} → {ClientLookup.YesNo(includeInterest.Value)}");
        }

        var attachPdf = OptionalFlag(parameters, "attach_invoice_pdf");
        if (attachPdf is not null && attachPdf != current.AttachInvoicePdf)
        {
            dto.AttachInvoicePdf = attachPdf.Value;
            changes.Add($"attach invoice PDF: {ClientLookup.YesNo(current.AttachInvoicePdf)} → {ClientLookup.YesNo(attachPdf.Value)}");
        }

        var autoSend = OptionalFlag(parameters, "auto_send_email");
        if (autoSend is not null && autoSend != current.AutoSendEmail)
        {
            dto.AutoSendEmail = autoSend.Value;
            changes.Add($"send e-mails automatically: {ClientLookup.YesNo(current.AutoSendEmail)} → {ClientLookup.YesNo(autoSend.Value)}");
        }

        return (dto, changes);
    }

    /// <summary>
    /// Lists the requested values as plain statements, for the case where nothing is stored
    /// yet and there is therefore no "from" value to compare against.
    /// </summary>
    private static List<string> DescribeRequested(Dictionary<string, string> parameters)
    {
        var requested = new List<string>();

        foreach (var parameter in Schema)
        {
            if (parameter.Type == ChatToolParameterType.Boolean)
            {
                if (OptionalFlag(parameters, parameter.Name) is { } flag)
                    requested.Add($"{parameter.Name} = {ClientLookup.YesNo(flag)}");
            }
            else if (OptionalWholeNumber(parameters, parameter.Name) is { } number)
            {
                requested.Add($"{parameter.Name} = {number}");
            }
        }

        return requested;
    }

    /// <summary>
    /// Reads an optional boolean parameter, or null when it was not sent.
    /// An unparsable value cannot reach this method — the executor rejects it as a type error
    /// before the tool runs — so null really does mean "leave this setting alone".
    /// </summary>
    private static bool? OptionalFlag(Dictionary<string, string> parameters, string name)
        => parameters.TryGetValue(name, out var raw) && bool.TryParse(raw.Trim(), out var flag)
            ? flag
            : null;

    /// <summary>
    /// Reads an optional whole-number parameter, or null when it was not sent.
    /// InvariantCulture because the model sends JSON numbers, never Czech-formatted ones.
    /// </summary>
    private static int? OptionalWholeNumber(Dictionary<string, string> parameters, string name)
        => parameters.TryGetValue(name, out var raw) &&
           int.TryParse(raw.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var number)
            ? number
            : null;
}

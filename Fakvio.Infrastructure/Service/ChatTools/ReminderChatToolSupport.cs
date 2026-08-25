using System.Globalization;
using System.Text;
using Fakvio.Contracts.Dto.Reminder;

namespace Fakvio.Infrastructure.Service.ChatTools;

/// <summary>
/// Shared wording for the two chat tools that talk about reminder (dunning) settings —
/// <see cref="GetReminderSettingsTool"/> and <see cref="UpdateReminderSettingsTool"/> (issue #227).
///
/// It exists so both tools describe the same record with the same words. The model only ever
/// sees this text, so a setting rendered one way when read and another way after a change
/// would read to the user as if something else had been changed.
/// </summary>
internal static class ReminderChatToolSupport
{
    /// <summary>
    /// Reported when the tenant has never saved reminder settings. Deliberately not the same
    /// as "reminders are switched off": nothing is stored at all, so the dunning job has no
    /// configuration to work from, and the message says where that is fixed.
    /// </summary>
    public const string NoSettingsStored =
        "No reminder (dunning) settings are stored for this company yet, so no reminders are " +
        "being generated. They are created the first time the settings are saved — on the " +
        "Reminders → Settings page (/reminders/settings), or by asking to change any reminder setting.";

    /// <summary>
    /// Renders the whole settings record as a text block — one fact per line, because this
    /// text is the model's only source for the answer and anything missing gets invented.
    /// </summary>
    public static string FormatSettings(ReminderSettingsDto settings)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"Reminder (dunning) settings — {DescribeScope(settings)}:");
        sb.AppendLine($"  Reminders enabled: {ClientLookup.YesNo(settings.IsEnabled)}");
        sb.AppendLine($"  Maximum escalation level: {settings.MaxReminderLevel}");
        sb.AppendLine($"  Grace period after due date: {settings.GracePeriodDays} days");
        sb.AppendLine($"  Include late payment interest: {ClientLookup.YesNo(settings.IncludeInterest)}");
        sb.AppendLine($"  Attach invoice PDF: {ClientLookup.YesNo(settings.AttachInvoicePdf)}");
        sb.AppendLine($"  Send e-mails automatically: {ClientLookup.YesNo(settings.AutoSendEmail)}" +
                      $" ({(settings.AutoSendEmail ? "reminders are e-mailed by the daily job" : "reminders are created as drafts for manual review")})");

        if (settings.Levels.Count == 0)
        {
            sb.AppendLine("  Escalation levels: none defined — no reminder can be generated.");
            return sb.ToString();
        }

        sb.AppendLine("  Escalation levels:");
        foreach (var level in settings.Levels.OrderBy(level => level.Level))
        {
            sb.AppendLine(
                $"    Level {level.Level}: {level.DaysAfterPrevious} days after the previous step | " +
                $"fee: {level.FixedFeeCzk.ToString("N2", CultureInfo.InvariantCulture)} CZK | subject: {level.Subject ?? "(template default)"}");
        }

        return sb.ToString();
    }

    /// <summary>
    /// Names the scope a settings record belongs to. The 2-tier resolution (client override
    /// wins over the company default) is invisible in the DTO itself — only
    /// <see cref="ReminderSettingsDto.ClientId"/> tells the two apart, and the user has to
    /// know which of the two they are looking at.
    /// </summary>
    public static string DescribeScope(ReminderSettingsDto settings)
        => settings.ClientId is null
            ? "company-wide default"
            : $"override for client '{settings.ClientName ?? "(unknown)"}' (ID {settings.ClientId})";
}

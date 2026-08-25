using System.Globalization;
using System.Text;
using Fakvio.Contracts.Dto.NumberSequence;
using Fakvio.Contracts.Dto.VatRate;

namespace Fakvio.Infrastructure.Service.ChatTools;

/// <summary>
/// Shared plumbing for the six settings chat tools of issue #224 — the ones that read and write
/// the tenant's number sequences (<see cref="ListNumberSequencesTool"/>,
/// <see cref="CreateNumberSequenceTool"/>, <see cref="UpdateNumberSequenceTool"/>) and its VAT
/// rates (<see cref="ListVatRatesTool"/>, <see cref="CreateVatRateTool"/>,
/// <see cref="UpdateVatRateTool"/>).
///
/// It exists for the same reason <see cref="IssuerChatToolSupport"/> does: the model never sees
/// the records, only the text these helpers render. A sequence described one way in the preview
/// and another way in the result reads to the user as if something else had been changed.
///
/// Junior note: the parameter readers below look like the ones in
/// <see cref="IssuerChatToolSupport"/> and <c>UpdateClientTool</c>. That repetition is the
/// established shape in this folder — each tool group owns its readers — and unifying them is a
/// change to shared chat-tool infrastructure, which this task deliberately keeps out of scope.
/// </summary>
internal static class SettingsChatToolSupport
{
    /// <summary>
    /// Refusal shared by both update tools when the model asks for <c>is_default: false</c>.
    ///
    /// Why refuse instead of just clearing the flag: neither a document type nor a VAT rate kind
    /// benefits from having no default at all — new invoices would silently lose their numbering
    /// or their tax rate. The default can be MOVED (send <c>is_default: true</c> on the record
    /// that should hold it), which is the same rule <c>update_bank_account</c> follows.
    /// </summary>
    public const string DefaultCannotBeCleared =
        "The default flag cannot be switched off, only moved. To change which record is the " +
        "default one, call this tool with is_default: true on the record that should become " +
        "the default instead.";

    /// <summary>Returned by both update tools when the call carries no field to change.</summary>
    public const string NothingToChange =
        "No change was requested — send at least one field with a new value.";

    /// <summary>
    /// Reports the half-done state of the two-step "update, then move the default" write: the
    /// record was already changed, but <c>SetAsDefaultAsync</c> found nothing to promote (the row
    /// disappeared between the two calls). The message has to name both halves — claiming plain
    /// success would hide the missing default, and claiming "no such record" would hide the write
    /// that did happen.
    /// </summary>
    public static string DefaultMoveFailedAfterUpdate(string recordDescription, long id)
        => $"The {recordDescription} was updated, but the default flag could not be moved to it — " +
           $"there is no longer a record with ID {id}. Call the matching list tool to see the " +
           "current state.";

    // ─── Parameter readers ────────────────────────────────────────────────
    //
    // Presence, allowed values and types are validated centrally by ChatToolExecutor before the
    // tool runs, so these never have to report a format error — a value that reaches them either
    // parses or was never sent.
    //
    // Trim on purpose: the executor validates the trimmed value but dispatches the raw one, so
    // a stray space from the model would otherwise be written to the database. Central
    // normalization in the executor is tracked as #268.

    /// <summary>
    /// Reads an optional text parameter, or null when the model did not send it.
    /// Blank counts as "not sent" — models happily send an empty string for a value they do not
    /// have, and treating that as a real value would wipe the stored field.
    /// </summary>
    public static string? OptionalText(Dictionary<string, string> parameters, string name)
        => parameters.TryGetValue(name, out var raw) && !string.IsNullOrWhiteSpace(raw)
            ? raw.Trim()
            : null;

    /// <summary>Reads an optional boolean parameter, or null when it was not sent.</summary>
    public static bool? OptionalFlag(Dictionary<string, string> parameters, string name)
        => parameters.TryGetValue(name, out var raw) && bool.TryParse(raw.Trim(), out var flag)
            ? flag
            : null;

    /// <summary>Reads an optional whole-number parameter, or null when it was not sent.</summary>
    public static int? OptionalInt(Dictionary<string, string> parameters, string name)
        => parameters.TryGetValue(name, out var raw)
           && int.TryParse(raw.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var value)
            ? value
            : null;

    /// <summary>Reads an optional decimal parameter, or null when it was not sent.</summary>
    public static decimal? OptionalNumber(Dictionary<string, string> parameters, string name)
        => parameters.TryGetValue(name, out var raw)
           && decimal.TryParse(raw.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out var value)
            ? value
            : null;

    /// <summary>
    /// Reads a required record id. Presence and the numeric format are guaranteed by the
    /// executor's central validation, so parsing cannot fail here.
    /// </summary>
    public static long RequiredId(Dictionary<string, string> parameters, string name)
        => long.Parse(parameters[name].Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture);

    /// <summary>Reads a required decimal parameter (the VAT percentage). See <see cref="RequiredId"/>.</summary>
    public static decimal RequiredNumber(Dictionary<string, string> parameters, string name)
        => decimal.Parse(parameters[name].Trim(), NumberStyles.Float, CultureInfo.InvariantCulture);

    // ─── Number sequences ─────────────────────────────────────────────────

    /// <summary>
    /// Renders the sequences as a text block, always including the id — it is the handle
    /// <c>update_number_sequence</c> needs, and the model can only quote an id it has been shown.
    /// </summary>
    public static string FormatSequences(IReadOnlyList<NumberSequenceDto> sequences)
    {
        if (sequences.Count == 0)
            return "Number sequences: none defined yet\n";

        var sb = new StringBuilder();
        sb.AppendLine("Number sequences:");
        foreach (var sequence in sequences)
            sb.AppendLine($"  {DescribeSequence(sequence)}");

        return sb.ToString();
    }

    /// <summary>
    /// One sequence on one line. <paramref name="includeId"/> is false for a sequence that does
    /// not exist yet (a create preview) — printing "ID=0" would offer the model a handle that
    /// leads nowhere.
    /// </summary>
    public static string DescribeSequence(NumberSequenceDto sequence, bool includeId = true)
    {
        var details = new List<string>
        {
            $"name: {sequence.Name}",
            $"document type: {sequence.DocumentType}"
        };

        if (!string.IsNullOrWhiteSpace(sequence.Prefix))
            details.Add($"prefix: {sequence.Prefix}");
        if (!string.IsNullOrWhiteSpace(sequence.Suffix))
            details.Add($"suffix: {sequence.Suffix}");

        details.Add($"format: {DescribePattern(sequence.NumberSequenceFormat)}");

        // The counter holds the number already used, so the next document gets CurrentNumber + 1.
        // Spelling both out keeps the model from reporting the counter as the next number.
        details.Add($"counter: {sequence.CurrentNumber} (next number {sequence.CurrentNumber + 1})");

        if (sequence.IsDefault)
            details.Add("default");
        if (!sequence.IsActive)
            details.Add("INACTIVE");

        return includeId
            ? $"ID={sequence.Id} | {string.Join(" | ", details)}"
            : string.Join(" | ", details);
    }

    /// <summary>
    /// Renders the numbering formats. <c>create_number_sequence</c> addresses a format by id,
    /// so the list tool prints them next to the sequences — otherwise the model has no way to
    /// learn a valid <c>format_id</c>.
    /// </summary>
    public static string FormatFormats(IReadOnlyList<NumberSequenceFormatDto> formats)
    {
        if (formats.Count == 0)
            return "Numbering formats: none defined yet\n";

        var sb = new StringBuilder();
        sb.AppendLine("Numbering formats (use the ID as format_id when creating a sequence):");
        foreach (var format in formats)
            sb.AppendLine($"  ID={format.Id} | {DescribePattern(format)}");

        return sb.ToString();
    }

    /// <summary>
    /// Describes one numbering format: its name, the raw pattern and when the counter restarts.
    /// The reset rule is the part a user cannot read off the pattern at a glance.
    /// </summary>
    public static string DescribePattern(NumberSequenceFormatDto format)
    {
        var reset = format switch
        {
            { ResetsMonthly: true } => "resets monthly",
            { ResetsYearly: true } => "resets yearly",
            _ => "continuous"
        };

        return $"{format.Name} [{format.FormatPattern}], {format.CounterDigits} counter digits, {reset}";
    }

    // ─── VAT rates ────────────────────────────────────────────────────────

    /// <summary>
    /// Renders the VAT rates as a text block, always including the id — the handle
    /// <c>update_vat_rate</c> needs.
    /// </summary>
    public static string FormatRates(IReadOnlyList<VatRateDto> rates)
    {
        if (rates.Count == 0)
            return "VAT rates: none defined yet\n";

        var sb = new StringBuilder();
        sb.AppendLine("VAT rates:");
        foreach (var rate in rates)
            sb.AppendLine($"  {DescribeRate(rate)}");

        return sb.ToString();
    }

    /// <summary>
    /// One VAT rate on one line. <paramref name="includeId"/> is false for a rate that does not
    /// exist yet (a create preview).
    /// </summary>
    public static string DescribeRate(VatRateDto rate, bool includeId = true)
    {
        var details = new List<string>
        {
            $"{FormatPercentage(rate.Rate)} %",
            $"name: {rate.Name}",
            rate.IsReduced ? "reduced" : "standard",
            $"valid {ChatToolDates.Format(rate.ValidFrom)} → {(rate.ValidTo.HasValue ? ChatToolDates.Format(rate.ValidTo) : "open-ended")}"
        };

        if (rate.IsDefault)
            details.Add("default");
        if (!rate.IsActive)
            details.Add("INACTIVE");

        return includeId
            ? $"ID={rate.Id} | {string.Join(" | ", details)}"
            : string.Join(" | ", details);
    }

    /// <summary>
    /// Formats the percentage without trailing zeros ("21", not "21.00") and culture-independently
    /// — the text goes to the model, which is told to work with plain decimal numbers.
    /// </summary>
    public static string FormatPercentage(decimal rate)
        => rate.ToString("0.##", CultureInfo.InvariantCulture);
}

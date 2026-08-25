using Fakvio.Application.Service;
using Fakvio.Contracts.Dto.NumberSequence;
using Microsoft.Extensions.Logging;

namespace Fakvio.Infrastructure.Service.ChatTools;

/// <summary>
/// Chat tool that changes one document number sequence (číselná řada), issue #224.
///
/// Typical usage:
///   "Přejmenuj řadu 2 na 'Faktury 2026' a dej jí prefix FV-."
///   "Nastav u faktur čítač na 100."
///   "Ať je řada 3 výchozí pro dobropisy."
///
/// Only the safe fields are editable — the same ones the UI offers. The document type and the
/// numbering format are NOT among them: changing either would rewrite the shape of numbers that
/// have already been issued, so a new sequence is the right answer there.
///
/// Data-changing, therefore <see cref="IConfirmableChatTool"/> — nothing is written before the
/// user approves the preview (DEVGUIDE §4.7).
/// </summary>
public class UpdateNumberSequenceTool : IConfirmableChatTool
{
    private readonly INumberSequenceService _numberSequenceService;
    private readonly ILogger<UpdateNumberSequenceTool> _logger;

    public UpdateNumberSequenceTool(
        INumberSequenceService numberSequenceService,
        ILogger<UpdateNumberSequenceTool> logger)
    {
        _numberSequenceService = numberSequenceService;
        _logger = logger;
    }

    public string ToolName => "update_number_sequence";

    public string Description =>
        "Change one document number sequence (číselná řada): its name, prefix, suffix, counter, " +
        "or make it the default sequence for its document type. Identify the sequence by the ID " +
        "returned from list_number_sequences and send only the fields that should change. The " +
        "document type and the numbering format cannot be changed — create a new sequence instead.";

    /// <summary>Parameter schema — static because it never changes per instance.</summary>
    private static readonly ChatToolParameter[] Schema =
    [
        new()
        {
            Name = "id",
            Type = ChatToolParameterType.Integer,
            Description = "ID of the number sequence to change, as returned by list_number_sequences",
            IsRequired = true
        },
        new()
        {
            Name = "name",
            Type = ChatToolParameterType.String,
            Description = "New name of the sequence"
        },
        new()
        {
            Name = "prefix",
            Type = ChatToolParameterType.String,
            Description = "New text prepended to every generated number, e.g. 'FV-'"
        },
        new()
        {
            Name = "suffix",
            Type = ChatToolParameterType.String,
            Description = "New text appended to every generated number"
        },
        new()
        {
            Name = "current_number",
            Type = ChatToolParameterType.Integer,
            Description = "New counter value. The next generated document gets this number plus one, " +
                          "so send 100 to make the next number 101."
        },
        new()
        {
            Name = "is_default",
            Type = ChatToolParameterType.Boolean,
            Description = "True to make this the default sequence for its document type. False is " +
                          "refused — a document type always needs a default, so it can only be moved"
        }
    ];

    public IReadOnlyList<ChatToolParameter> Parameters => Schema;

    public async Task<ChatToolResult> BuildPreviewAsync(
        Dictionary<string, string> parameters,
        CancellationToken ct = default)
    {
        var (sequence, failure) = await ResolveAsync(parameters, ct);
        if (failure is not null)
            return failure;

        var changes = DescribeChanges(sequence!, parameters);
        if (changes.Count == 0)
            return ChatToolResult.Failure(SettingsChatToolSupport.NothingToChange);

        return ChatToolResult.Success(
            $"Number sequence ID={sequence!.Id} ('{sequence.Name}') would change:\n" +
            string.Join("\n", changes.Select(change => $"  - {change}")));
    }

    public async Task<ChatToolResult> ExecuteAsync(
        Dictionary<string, string> parameters,
        CancellationToken ct = default)
    {
        var sequenceId = SettingsChatToolSupport.RequiredId(parameters, "id");
        _logger.LogInformation("UpdateNumberSequenceTool executing for sequence {SequenceId}", sequenceId);

        // Re-read: preview and execution are separate calls and nothing correlates them, so the
        // sequence may have been changed or removed in between.
        var (sequence, failure) = await ResolveAsync(parameters, ct);
        if (failure is not null)
            return failure;

        var changes = DescribeChanges(sequence!, parameters);
        if (changes.Count == 0)
            return ChatToolResult.Failure(SettingsChatToolSupport.NothingToChange);

        var updateDto = BuildUpdate(parameters);
        var updated = sequence;

        // Only touch the record when a plain field really changed — otherwise "make this the
        // default" alone would issue a pointless write.
        if (updateDto is not null)
        {
            updated = await _numberSequenceService.UpdateSequenceAsync(sequenceId, updateDto, ct);
            if (updated is null)
                return ChatToolResult.Failure(NotFound(sequenceId));
        }

        // The default flag has no place in UpdateNumberSequenceDto — moving it is a separate
        // operation that also unsets the previous holder for the same document type.
        if (SettingsChatToolSupport.OptionalFlag(parameters, "is_default") == true && !sequence!.IsDefault)
        {
            updated = await _numberSequenceService.SetAsDefaultAsync(sequenceId, ct);
            if (updated is null)
                return ChatToolResult.Failure(NotFound(sequenceId));
        }

        _logger.LogInformation("Number sequence {SequenceId} updated", sequenceId);

        return ChatToolResult.Success(
            $"Number sequence updated ({string.Join("; ", changes)}).\n" +
            $"  {SettingsChatToolSupport.DescribeSequence(updated!)}");
    }

    /// <summary>
    /// Loads the sequence the call addresses and rejects the two things the schema cannot check:
    /// an unknown ID and the attempt to switch the default flag off.
    /// </summary>
    private async Task<(NumberSequenceDto? Sequence, ChatToolResult? Failure)> ResolveAsync(
        Dictionary<string, string> parameters,
        CancellationToken ct)
    {
        if (SettingsChatToolSupport.OptionalFlag(parameters, "is_default") == false)
            return (null, ChatToolResult.Failure(SettingsChatToolSupport.DefaultCannotBeCleared));

        // A negative counter would make the next generated number zero or lower — nonsense the
        // service does not guard against, so it is rejected here rather than stored.
        if (SettingsChatToolSupport.OptionalInt(parameters, "current_number") is { } counter && counter < 0)
            return (null, ChatToolResult.Failure(
                $"The counter cannot be set to {counter} — the lowest valid value is 0, which makes " +
                "the next generated number 1."));

        var sequenceId = SettingsChatToolSupport.RequiredId(parameters, "id");

        var sequence = await _numberSequenceService.GetSequenceByIdAsync(sequenceId, ct);
        return sequence is null
            ? (null, ChatToolResult.Failure(NotFound(sequenceId)))
            : (sequence, null);
    }

    /// <summary>
    /// Lists the changes in "was → will be" form. Producing the list is also how the tool decides
    /// there is anything to do at all, so the preview and the write can never disagree about it.
    /// </summary>
    private static List<string> DescribeChanges(NumberSequenceDto sequence, Dictionary<string, string> parameters)
    {
        var changes = new List<string>();

        if (SettingsChatToolSupport.OptionalText(parameters, "name") is { } name && name != sequence.Name)
            changes.Add($"name: '{sequence.Name}' → '{name}'");

        if (SettingsChatToolSupport.OptionalText(parameters, "prefix") is { } prefix && prefix != sequence.Prefix)
            changes.Add($"prefix: '{sequence.Prefix ?? string.Empty}' → '{prefix}'");

        if (SettingsChatToolSupport.OptionalText(parameters, "suffix") is { } suffix && suffix != sequence.Suffix)
            changes.Add($"suffix: '{sequence.Suffix ?? string.Empty}' → '{suffix}'");

        if (SettingsChatToolSupport.OptionalInt(parameters, "current_number") is { } counter &&
            counter != sequence.CurrentNumber)
        {
            // Lowering the counter re-issues numbers that are already on documents. The user has
            // to see that BEFORE confirming — afterwards it is a duplicate invoice number.
            var warning = counter < sequence.CurrentNumber
                ? " — WARNING: this goes back, so numbers already used would be issued a second time"
                : string.Empty;

            changes.Add(
                $"counter: {sequence.CurrentNumber} → {counter} " +
                $"(next number {counter + 1}){warning}");
        }

        if (SettingsChatToolSupport.OptionalFlag(parameters, "is_default") == true && !sequence.IsDefault)
            changes.Add($"it would become the default sequence for {sequence.DocumentType}");

        return changes;
    }

    /// <summary>
    /// Builds the update DTO, or null when the call only moves the default flag.
    ///
    /// Only fields the model actually sent are filled in: <c>UpdateSequenceAsync</c> applies
    /// every non-null property, so carrying a value the user did not mention would overwrite a
    /// field that meanwhile changed elsewhere.
    /// </summary>
    private static UpdateNumberSequenceDto? BuildUpdate(Dictionary<string, string> parameters)
    {
        var dto = new UpdateNumberSequenceDto
        {
            Name = SettingsChatToolSupport.OptionalText(parameters, "name"),
            Prefix = SettingsChatToolSupport.OptionalText(parameters, "prefix"),
            Suffix = SettingsChatToolSupport.OptionalText(parameters, "suffix"),
            CurrentNumber = SettingsChatToolSupport.OptionalInt(parameters, "current_number")
        };

        return dto is { Name: null, Prefix: null, Suffix: null, CurrentNumber: null } ? null : dto;
    }

    private static string NotFound(long sequenceId)
        => $"There is no number sequence with ID {sequenceId}. Call list_number_sequences to see " +
           "the existing ones.";
}

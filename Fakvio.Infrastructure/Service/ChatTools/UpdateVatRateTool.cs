using Fakvio.Application.Service;
using Fakvio.Contracts.Dto.VatRate;
using Microsoft.Extensions.Logging;

namespace Fakvio.Infrastructure.Service.ChatTools;

/// <summary>
/// Chat tool that changes one VAT rate (sazba DPH), issue #224.
///
/// Typical usage:
///   "U sazby 2 oprav platnost do 31.12.2026."
///   "Ať je 21 % výchozí základní sazba."
///
/// Deactivating a rate is NOT here: that is the soft delete of
/// <c>IVatRateService.DeleteVatRateAsync</c>, and deleting through the assistant is out of scope
/// for issue #224.
///
/// Data-changing, therefore <see cref="IConfirmableChatTool"/> — nothing is written before the
/// user approves the preview (DEVGUIDE §4.7).
/// </summary>
public class UpdateVatRateTool : IConfirmableChatTool
{
    private readonly IVatRateService _vatRateService;
    private readonly ILogger<UpdateVatRateTool> _logger;

    public UpdateVatRateTool(IVatRateService vatRateService, ILogger<UpdateVatRateTool> logger)
    {
        _vatRateService = vatRateService;
        _logger = logger;
    }

    public string ToolName => "update_vat_rate";

    public string Description =>
        "Change one VAT rate (sazba DPH): its name, percentage, validity dates, whether it is a " +
        "reduced rate, or make it the default rate of its kind. Identify the rate by the ID " +
        "returned from list_vat_rates and send only the fields that should change.";

    /// <summary>Parameter schema — static because it never changes per instance.</summary>
    private static readonly ChatToolParameter[] Schema =
    [
        new()
        {
            Name = "id",
            Type = ChatToolParameterType.Integer,
            Description = "ID of the VAT rate to change, as returned by list_vat_rates",
            IsRequired = true
        },
        new()
        {
            Name = "name",
            Type = ChatToolParameterType.String,
            Description = "New name of the rate, e.g. 'DPH 21% základní'"
        },
        new()
        {
            Name = "rate",
            Type = ChatToolParameterType.Number,
            Description = "New percentage, e.g. 21 for 21 %"
        },
        new()
        {
            Name = "valid_from",
            Type = ChatToolParameterType.String,
            Description = "New first day the rate applies, in YYYY-MM-DD format"
        },
        new()
        {
            Name = "valid_to",
            Type = ChatToolParameterType.String,
            Description = "New last day the rate applies, in YYYY-MM-DD format"
        },
        new()
        {
            Name = "is_reduced",
            Type = ChatToolParameterType.Boolean,
            Description = "True to mark the rate as reduced (snížená sazba), false as standard. " +
                          "Refused on the rate that currently holds the default flag — move the " +
                          "default to another rate of that kind first"
        },
        new()
        {
            Name = "is_default",
            Type = ChatToolParameterType.Boolean,
            Description = "True to make this the default rate of its kind. False is refused — the " +
                          "default cannot be switched off, only moved to another rate"
        }
    ];

    public IReadOnlyList<ChatToolParameter> Parameters => Schema;

    public async Task<ChatToolResult> BuildPreviewAsync(
        Dictionary<string, string> parameters,
        CancellationToken ct = default)
    {
        var (current, merged, failure) = await ResolveAsync(parameters, ct);
        if (failure is not null)
            return failure;

        var changes = DescribeChanges(current!, merged!, parameters);
        if (changes.Count == 0)
            return ChatToolResult.Failure(SettingsChatToolSupport.NothingToChange);

        return ChatToolResult.Success(
            $"VAT rate ID={current!.Id} ('{current.Name}') would change:\n" +
            string.Join("\n", changes.Select(change => $"  - {change}")));
    }

    public async Task<ChatToolResult> ExecuteAsync(
        Dictionary<string, string> parameters,
        CancellationToken ct = default)
    {
        var vatRateId = SettingsChatToolSupport.RequiredId(parameters, "id");
        _logger.LogInformation("UpdateVatRateTool executing for rate {VatRateId}", vatRateId);

        // Re-read: preview and execution are separate calls and nothing correlates them, so the
        // rate may have been changed or removed in between.
        var (current, merged, failure) = await ResolveAsync(parameters, ct);
        if (failure is not null)
            return failure;

        var changes = DescribeChanges(current!, merged!, parameters);
        if (changes.Count == 0)
            return ChatToolResult.Failure(SettingsChatToolSupport.NothingToChange);

        var updated = await _vatRateService.UpdateVatRateAsync(vatRateId, merged!, ct);
        if (updated is null)
            return ChatToolResult.Failure(NotFound(vatRateId));

        // Moving the default is a separate call for the same reason as in create_vat_rate:
        // UpdateVatRateAsync refuses a second default of the same kind, SetAsDefaultAsync unsets
        // the previous holder instead.
        if (SettingsChatToolSupport.OptionalFlag(parameters, "is_default") == true && !current!.IsDefault)
        {
            var promoted = await _vatRateService.SetAsDefaultAsync(vatRateId, ct);
            if (promoted is null)
                return ChatToolResult.Failure(
                    SettingsChatToolSupport.DefaultMoveFailedAfterUpdate("VAT rate", vatRateId));

            updated = promoted;
        }

        _logger.LogInformation("VAT rate {VatRateId} updated", vatRateId);

        return ChatToolResult.Success(
            $"VAT rate updated ({string.Join("; ", changes)}).\n" +
            $"  {SettingsChatToolSupport.DescribeRate(updated)}");
    }

    /// <summary>
    /// Loads the stored rate and merges the supplied fields into the update DTO.
    ///
    /// The merge is necessary because <c>UpdateVatRateAsync</c> overwrites every property from
    /// the DTO — a field left at its default would silently erase the stored value. Everything
    /// the model did not send is therefore carried over unchanged.
    /// </summary>
    private async Task<(VatRateDto? Current, UpdateVatRateDto? Merged, ChatToolResult? Failure)> ResolveAsync(
        Dictionary<string, string> parameters,
        CancellationToken ct)
    {
        if (SettingsChatToolSupport.OptionalFlag(parameters, "is_default") == false)
            return (null, null, ChatToolResult.Failure(SettingsChatToolSupport.DefaultCannotBeCleared));

        var vatRateId = SettingsChatToolSupport.RequiredId(parameters, "id");

        var current = await _vatRateService.GetVatRateByIdAsync(vatRateId, ct);
        if (current is null)
            return (null, null, ChatToolResult.Failure(NotFound(vatRateId)));

        // The default flag belongs to a kind ("default standard rate" / "default reduced rate"),
        // so flipping the kind of the rate that currently holds it takes the flag along and leaves
        // the kind it came from without a default — the exact state DefaultCannotBeCleared refuses,
        // just reached through is_reduced instead of is_default. Refused here, before the preview,
        // because afterwards the service either throws (the other kind already has a default) or
        // performs the write and leaves the hole.
        var isReduced = SettingsChatToolSupport.OptionalFlag(parameters, "is_reduced") ?? current.IsReduced;
        if (current.IsDefault && isReduced != current.IsReduced)
            return (null, null, ChatToolResult.Failure(DefaultRateCannotChangeKind));

        var rate = SettingsChatToolSupport.OptionalNumber(parameters, "rate") ?? current.Rate;
        if (rate is < 0 or > 100)
            return (null, null, ChatToolResult.Failure(
                $"A VAT rate of {SettingsChatToolSupport.FormatPercentage(rate)} % is not valid — " +
                "the percentage must be between 0 and 100."));

        if (!ChatToolDates.TryParseOptional(parameters, "valid_from", out var validFrom, out var dateError) ||
            !ChatToolDates.TryParseOptional(parameters, "valid_to", out var validTo, out dateError))
        {
            return (null, null, ChatToolResult.Failure(dateError!));
        }

        var merged = new UpdateVatRateDto
        {
            Name = SettingsChatToolSupport.OptionalText(parameters, "name") ?? current.Name,
            Rate = rate,
            ValidFrom = validFrom ?? current.ValidFrom,
            ValidTo = validTo ?? current.ValidTo,
            IsReduced = isReduced,

            // The default flag is carried over, never raised here — moving it is the separate
            // SetAsDefaultAsync call in ExecuteAsync.
            IsDefault = current.IsDefault,

            // Deactivating a rate is the soft delete, which this tool does not offer.
            IsActive = current.IsActive
        };

        if (merged.ValidTo.HasValue && merged.ValidTo.Value < merged.ValidFrom)
            return (null, null, ChatToolResult.Failure(
                $"The validity window is backwards: valid_to ({ChatToolDates.Format(merged.ValidTo)}) is " +
                $"before valid_from ({ChatToolDates.Format(merged.ValidFrom)})."));

        return (current, merged, null);
    }

    /// <summary>
    /// Lists the changes in "was → will be" form. Producing the list is also how the tool decides
    /// there is anything to do at all, so the preview and the write can never disagree about it.
    /// </summary>
    private static List<string> DescribeChanges(
        VatRateDto current,
        UpdateVatRateDto merged,
        Dictionary<string, string> parameters)
    {
        var changes = new List<string>();

        if (merged.Name != current.Name)
            changes.Add($"name: '{current.Name}' → '{merged.Name}'");

        if (merged.Rate != current.Rate)
            changes.Add(
                $"rate: {SettingsChatToolSupport.FormatPercentage(current.Rate)} % → " +
                $"{SettingsChatToolSupport.FormatPercentage(merged.Rate)} %");

        if (merged.ValidFrom != current.ValidFrom)
            changes.Add($"valid from: {ChatToolDates.Format(current.ValidFrom)} → {ChatToolDates.Format(merged.ValidFrom)}");

        if (merged.ValidTo != current.ValidTo)
            changes.Add($"valid to: {ChatToolDates.Format(current.ValidTo)} → {ChatToolDates.Format(merged.ValidTo)}");

        if (merged.IsReduced != current.IsReduced)
            changes.Add($"kind: {(current.IsReduced ? "reduced" : "standard")} → {(merged.IsReduced ? "reduced" : "standard")}");

        if (SettingsChatToolSupport.OptionalFlag(parameters, "is_default") == true && !current.IsDefault)
            changes.Add($"it would become the default {(merged.IsReduced ? "reduced" : "standard")} rate");

        return changes;
    }

    /// <summary>
    /// Refusal for "make the default rate the other kind" — the sibling of
    /// <see cref="SettingsChatToolSupport.DefaultCannotBeCleared"/>, worded the same way: the
    /// default is moved, never dropped.
    /// </summary>
    private const string DefaultRateCannotChangeKind =
        "This rate is currently the default one of its kind, so it cannot be switched between " +
        "standard and reduced — the kind it left would stay without a default rate. Make another " +
        "rate of that kind the default first (update_vat_rate with is_default: true on it), then " +
        "change this one.";

    private static string NotFound(long vatRateId)
        => $"There is no VAT rate with ID {vatRateId}. Call list_vat_rates to see the existing ones.";
}

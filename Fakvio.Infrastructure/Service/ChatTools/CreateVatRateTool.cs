using Fakvio.Application.Service;
using Fakvio.Contracts.Dto.VatRate;
using Microsoft.Extensions.Logging;

namespace Fakvio.Infrastructure.Service.ChatTools;

/// <summary>
/// Chat tool that creates a new VAT rate (sazba DPH), issue #224.
///
/// Typical usage:
///   "Přidej sníženou sazbu 12 % platnou od 1.1.2026."
///   "Založ sazbu 21 % a ať je výchozí."
///
/// Data-changing, therefore <see cref="IConfirmableChatTool"/> — nothing is written before the
/// user approves the preview (DEVGUIDE §4.7).
/// </summary>
public class CreateVatRateTool : IConfirmableChatTool
{
    private readonly IVatRateService _vatRateService;
    private readonly ILogger<CreateVatRateTool> _logger;

    public CreateVatRateTool(IVatRateService vatRateService, ILogger<CreateVatRateTool> logger)
    {
        _vatRateService = vatRateService;
        _logger = logger;
    }

    public string ToolName => "create_vat_rate";

    public string Description =>
        "Create a new VAT rate (sazba DPH) for the user's company. The percentage and a name are " +
        "required; validity starts today unless a date is given. A rate can be marked as the " +
        "default standard or default reduced rate, which is the one offered on new invoice items.";

    /// <summary>Parameter schema — static because it never changes per instance.</summary>
    private static readonly ChatToolParameter[] Schema =
    [
        new()
        {
            Name = "name",
            Type = ChatToolParameterType.String,
            Description = "Name of the rate, e.g. 'DPH 21% základní'",
            IsRequired = true
        },
        new()
        {
            Name = "rate",
            Type = ChatToolParameterType.Number,
            Description = "The percentage itself, e.g. 21 for 21 %",
            IsRequired = true
        },
        new()
        {
            Name = "valid_from",
            Type = ChatToolParameterType.String,
            Description = "First day the rate applies, in YYYY-MM-DD format (default today)"
        },
        new()
        {
            Name = "valid_to",
            Type = ChatToolParameterType.String,
            Description = "Last day the rate applies, in YYYY-MM-DD format. Omit for an open-ended rate."
        },
        new()
        {
            Name = "is_reduced",
            Type = ChatToolParameterType.Boolean,
            Description = "True for a reduced rate (snížená sazba), false for a standard one (default false)"
        },
        new()
        {
            Name = "is_default",
            Type = ChatToolParameterType.Boolean,
            Description = "True to make this the default rate of its kind, replacing the current default"
        }
    ];

    public IReadOnlyList<ChatToolParameter> Parameters => Schema;

    public async Task<ChatToolResult> BuildPreviewAsync(
        Dictionary<string, string> parameters,
        CancellationToken ct = default)
    {
        var (createDto, failure) = Resolve(parameters);
        if (failure is not null)
            return failure;

        var wantsDefault = SettingsChatToolSupport.OptionalFlag(parameters, "is_default") ?? false;
        var currentDefault = wantsDefault ? await GetDefaultAsync(createDto!.IsReduced, ct) : null;

        return ChatToolResult.Success(
            "A new VAT rate would be created:\n" +
            $"  {SettingsChatToolSupport.DescribeRate(Describe(createDto!, wantsDefault), includeId: false)}\n" +
            DescribeDefaultEffect(createDto!, wantsDefault, currentDefault));
    }

    public async Task<ChatToolResult> ExecuteAsync(
        Dictionary<string, string> parameters,
        CancellationToken ct = default)
    {
        _logger.LogInformation("CreateVatRateTool executing");

        var (createDto, failure) = Resolve(parameters);
        if (failure is not null)
            return failure;

        var created = await _vatRateService.CreateVatRateAsync(createDto!, ct);

        // The default flag is moved in a second step on purpose: CreateVatRateAsync REFUSES to
        // create a second default of the same kind, while SetAsDefaultAsync unsets the previous
        // holder — which is what "ať je výchozí" actually means to the user.
        if (SettingsChatToolSupport.OptionalFlag(parameters, "is_default") == true)
            created = await _vatRateService.SetAsDefaultAsync(created.Id, ct) ?? created;

        _logger.LogInformation("VAT rate {VatRateId} created", created.Id);

        return ChatToolResult.Success(
            $"VAT rate created.\n  {SettingsChatToolSupport.DescribeRate(created)}");
    }

    /// <summary>
    /// Builds the DTO and checks what the schema cannot: a percentage outside 0–100 and a
    /// validity window that ends before it starts. Both are rejected here rather than deep in
    /// the service, so the preview never promises a rate that the write would refuse.
    ///
    /// <c>IsDefault</c> is deliberately left false — see the note in <see cref="ExecuteAsync"/>.
    /// </summary>
    private static (CreateVatRateDto? Dto, ChatToolResult? Failure) Resolve(Dictionary<string, string> parameters)
    {
        // Required parameter — presence and numeric format are guaranteed by the executor.
        var rate = SettingsChatToolSupport.RequiredNumber(parameters, "rate");
        if (rate is < 0 or > 100)
            return (null, ChatToolResult.Failure(
                $"A VAT rate of {SettingsChatToolSupport.FormatPercentage(rate)} % is not valid — " +
                "the percentage must be between 0 and 100."));

        if (!ChatToolDates.TryParseOptional(parameters, "valid_from", out var validFrom, out var dateError) ||
            !ChatToolDates.TryParseOptional(parameters, "valid_to", out var validTo, out dateError))
        {
            return (null, ChatToolResult.Failure(dateError!));
        }

        // A rate the user does not date starts applying today — the alternative (refusing) would
        // force a date into every "přidej sazbu 12 %" the model cannot answer on its own.
        var effectiveFrom = validFrom ?? DateTime.UtcNow.Date;

        if (validTo.HasValue && validTo.Value < effectiveFrom)
            return (null, ChatToolResult.Failure(
                $"The validity window is backwards: valid_to ({ChatToolDates.Format(validTo)}) is " +
                $"before valid_from ({ChatToolDates.Format(effectiveFrom)})."));

        var dto = new CreateVatRateDto
        {
            Name = parameters["name"].Trim(),
            Rate = rate,
            ValidFrom = effectiveFrom,
            ValidTo = validTo,
            IsReduced = SettingsChatToolSupport.OptionalFlag(parameters, "is_reduced") ?? false,
            IsDefault = false,
            IsActive = true
        };

        return (dto, null);
    }

    /// <summary>Reads the rate that currently holds the default flag for this kind, if any.</summary>
    private Task<VatRateDto?> GetDefaultAsync(bool isReduced, CancellationToken ct)
        => isReduced
            ? _vatRateService.GetDefaultReducedRateAsync(ct)
            : _vatRateService.GetDefaultStandardRateAsync(ct);

    /// <summary>
    /// Renders the not-yet-stored rate with the same wording the stored ones get. The default
    /// flag is shown as the user asked for it, even though the write sets it in a second step.
    /// </summary>
    private static VatRateDto Describe(CreateVatRateDto dto, bool wantsDefault)
        => new()
        {
            Name = dto.Name,
            Rate = dto.Rate,
            ValidFrom = dto.ValidFrom,
            ValidTo = dto.ValidTo,
            IsReduced = dto.IsReduced,
            IsDefault = wantsDefault,
            IsActive = dto.IsActive
        };

    /// <summary>
    /// Spells out the side effect the user cannot see coming: which rate stops being the default.
    /// </summary>
    private static string DescribeDefaultEffect(CreateVatRateDto dto, bool wantsDefault, VatRateDto? currentDefault)
    {
        var kind = dto.IsReduced ? "reduced" : "standard";

        if (!wantsDefault)
            return $"  The current default {kind} rate stays unchanged.";

        return currentDefault is null
            ? $"  It would become the DEFAULT {kind} rate (the company has none yet)."
            : $"  It would become the DEFAULT {kind} rate, replacing " +
              $"'{currentDefault.Name}' (ID={currentDefault.Id}).";
    }
}

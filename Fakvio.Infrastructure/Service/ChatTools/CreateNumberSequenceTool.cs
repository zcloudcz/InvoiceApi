using Fakvio.Application.Service;
using Fakvio.Contracts.Dto.NumberSequence;
using Fakvio.Domain.Enums;
using Microsoft.Extensions.Logging;

namespace Fakvio.Infrastructure.Service.ChatTools;

/// <summary>
/// Chat tool that creates a new document number sequence (číselná řada), issue #224.
///
/// Typical usage:
///   "Založ nám řadu faktur 2026 s prefixem FV- a formátem 1."
///   "Chceme vlastní řadu na dobropisy, ať je výchozí."
///
/// Data-changing, therefore <see cref="IConfirmableChatTool"/> — nothing is written before the
/// user approves the preview (DEVGUIDE §4.7).
/// </summary>
public class CreateNumberSequenceTool : IConfirmableChatTool
{
    private readonly INumberSequenceService _numberSequenceService;
    private readonly ILogger<CreateNumberSequenceTool> _logger;

    public CreateNumberSequenceTool(
        INumberSequenceService numberSequenceService,
        ILogger<CreateNumberSequenceTool> logger)
    {
        _numberSequenceService = numberSequenceService;
        _logger = logger;
    }

    public string ToolName => "create_number_sequence";

    public string Description =>
        "Create a new document number sequence (číselná řada) for invoices, credit notes, " +
        "pro-forma invoices or advance tax receipts. The numbering format is referenced by the " +
        "ID that list_number_sequences prints, so call that tool first.";

    /// <summary>Parameter schema — static because it never changes per instance.</summary>
    private static readonly ChatToolParameter[] Schema =
    [
        new()
        {
            Name = "name",
            Type = ChatToolParameterType.String,
            Description = "Name of the sequence, e.g. 'Faktury 2026'",
            IsRequired = true
        },
        new()
        {
            Name = "document_type",
            Type = ChatToolParameterType.String,
            Description = "Document type the sequence numbers",
            IsRequired = true,
            AllowedValues = ["Invoice", "CreditNote", "Proforma", "TaxReceiptForAdvance"]
        },
        new()
        {
            Name = "format_id",
            Type = ChatToolParameterType.Integer,
            Description = "ID of the numbering format, as returned by list_number_sequences",
            IsRequired = true
        },
        new()
        {
            Name = "prefix",
            Type = ChatToolParameterType.String,
            Description = "Text prepended to every generated number, e.g. 'FV-'"
        },
        new()
        {
            Name = "suffix",
            Type = ChatToolParameterType.String,
            Description = "Text appended to every generated number"
        },
        new()
        {
            Name = "starting_number",
            Type = ChatToolParameterType.Integer,
            Description = "First number the sequence will generate (default 1)"
        },
        new()
        {
            Name = "is_default",
            Type = ChatToolParameterType.Boolean,
            Description = "True to make this the sequence used automatically for the document " +
                          "type, replacing the current default one"
        }
    ];

    public IReadOnlyList<ChatToolParameter> Parameters => Schema;

    public async Task<ChatToolResult> BuildPreviewAsync(
        Dictionary<string, string> parameters,
        CancellationToken ct = default)
    {
        var (createDto, format, failure) = await ResolveAsync(parameters, ct);
        if (failure is not null)
            return failure;

        var currentDefault = await _numberSequenceService.GetDefaultSequenceAsync(createDto!.DocumentType, ct);

        return ChatToolResult.Success(
            "A new number sequence would be created:\n" +
            $"  {SettingsChatToolSupport.DescribeSequence(Describe(createDto, format!), includeId: false)}\n" +
            DescribeDefaultEffect(createDto, currentDefault));
    }

    public async Task<ChatToolResult> ExecuteAsync(
        Dictionary<string, string> parameters,
        CancellationToken ct = default)
    {
        _logger.LogInformation("CreateNumberSequenceTool executing");

        // Re-validate: preview and execution are separate calls with anything in between, so
        // nothing from the preview may be assumed to still hold (the format may be gone).
        var (createDto, _, failure) = await ResolveAsync(parameters, ct);
        if (failure is not null)
            return failure;

        var created = await _numberSequenceService.CreateSequenceAsync(createDto!, ct);

        _logger.LogInformation("Number sequence {SequenceId} created", created.Id);

        return ChatToolResult.Success(
            $"Number sequence created.\n  {SettingsChatToolSupport.DescribeSequence(created)}");
    }

    /// <summary>
    /// Builds the DTO from the parameters and checks everything the schema cannot: that the
    /// referenced numbering format exists and that the sequence does not start below 1.
    /// Shared by the preview and the write so the preview describes exactly what would be stored.
    /// </summary>
    private async Task<(CreateNumberSequenceDto? Dto, NumberSequenceFormatDto? Format, ChatToolResult? Failure)>
        ResolveAsync(Dictionary<string, string> parameters, CancellationToken ct)
    {
        // Required parameters — presence, type and allowed values guaranteed by the executor.
        var documentType = Enum.Parse<EDocumentType>(parameters["document_type"].Trim(), ignoreCase: true);
        var formatId = SettingsChatToolSupport.RequiredId(parameters, "format_id");

        var startingNumber = SettingsChatToolSupport.OptionalInt(parameters, "starting_number") ?? 1;
        if (startingNumber < 1)
            return (null, null, ChatToolResult.Failure(
                $"The sequence cannot start at {startingNumber} — the first generated number must be 1 or higher."));

        var format = await _numberSequenceService.GetFormatByIdAsync(formatId, ct);
        if (format is null)
            return (null, null, ChatToolResult.Failure(
                $"There is no numbering format with ID {formatId}. Call list_number_sequences to " +
                "see the available format IDs."));

        var dto = new CreateNumberSequenceDto
        {
            Name = parameters["name"].Trim(),
            DocumentType = documentType,
            Prefix = SettingsChatToolSupport.OptionalText(parameters, "prefix"),
            Suffix = SettingsChatToolSupport.OptionalText(parameters, "suffix"),
            StartingNumber = startingNumber,
            IsDefault = SettingsChatToolSupport.OptionalFlag(parameters, "is_default") ?? false,
            NumberSequenceFormatId = formatId
        };

        return (dto, format, null);
    }

    /// <summary>
    /// Renders the not-yet-stored sequence with the same wording the stored ones get.
    /// The counter starts one below the first number, exactly as <c>CreateSequenceAsync</c>
    /// stores it, so the preview shows the same "next number" the user will really get.
    /// </summary>
    private static NumberSequenceDto Describe(CreateNumberSequenceDto dto, NumberSequenceFormatDto format)
        => new()
        {
            Name = dto.Name,
            DocumentType = dto.DocumentType,
            Prefix = dto.Prefix,
            Suffix = dto.Suffix,
            CurrentNumber = dto.StartingNumber - 1,
            IsDefault = dto.IsDefault,
            NumberSequenceFormatId = dto.NumberSequenceFormatId,
            NumberSequenceFormat = format,
            IsActive = true
        };

    /// <summary>
    /// Spells out the one consequence the user cannot see coming: which sequence the document
    /// type will use from now on. A new sequence does NOT become the default on its own — unlike
    /// bank accounts — so a company left without a default has to be told.
    /// </summary>
    private static string DescribeDefaultEffect(CreateNumberSequenceDto dto, NumberSequenceDto? currentDefault)
    {
        if (dto.IsDefault)
        {
            return currentDefault is null
                ? $"  It would become the DEFAULT sequence for {dto.DocumentType} (the company has none yet)."
                : $"  It would become the DEFAULT sequence for {dto.DocumentType}, replacing " +
                  $"'{currentDefault.Name}' (ID={currentDefault.Id}).";
        }

        return currentDefault is null
            ? $"  No sequence is the default for {dto.DocumentType}, and this one would not become it " +
              "either — new documents of that type would still have no numbering. Send is_default: " +
              "true to change that."
            : $"  '{currentDefault.Name}' (ID={currentDefault.Id}) stays the default sequence for {dto.DocumentType}.";
    }
}

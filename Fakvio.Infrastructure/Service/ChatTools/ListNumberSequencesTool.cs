using Fakvio.Application.Service;
using Fakvio.Domain.Enums;
using Microsoft.Extensions.Logging;

namespace Fakvio.Infrastructure.Service.ChatTools;

/// <summary>
/// Chat tool that lists the tenant's number sequences — the "číselné řady" from story #149,
/// issue #224.
///
/// Typical usage:
///   "Jaké máme číselné řady?"
///   "Na kolika je čítač u faktur?"
///
/// It also prints the available numbering FORMATS, because
/// <see cref="CreateNumberSequenceTool"/> addresses a format by id and the model has nowhere
/// else to learn a valid one.
///
/// Read-only, so no confirmation step (DEVGUIDE §4.7).
/// </summary>
public class ListNumberSequencesTool : IChatTool
{
    private readonly INumberSequenceService _numberSequenceService;
    private readonly ILogger<ListNumberSequencesTool> _logger;

    public ListNumberSequencesTool(
        INumberSequenceService numberSequenceService,
        ILogger<ListNumberSequencesTool> logger)
    {
        _numberSequenceService = numberSequenceService;
        _logger = logger;
    }

    public string ToolName => "list_number_sequences";

    public string Description =>
        "List the document number sequences (číselné řady) of the user's company: their name, " +
        "document type, prefix/suffix, current counter, numbering format and which one is the " +
        "default. Also lists the available numbering formats with the IDs needed to create a " +
        "new sequence.";

    /// <summary>Parameter schema — static because it never changes per instance.</summary>
    private static readonly ChatToolParameter[] Schema =
    [
        new()
        {
            Name = "document_type",
            Type = ChatToolParameterType.String,
            Description = "Show only sequences of this document type. Omit to list all of them.",
            AllowedValues = ["Invoice", "CreditNote", "Proforma", "TaxReceiptForAdvance"]
        },
        new()
        {
            Name = "include_inactive",
            Type = ChatToolParameterType.Boolean,
            Description = "True to include deactivated sequences and formats (default false)"
        }
    ];

    public IReadOnlyList<ChatToolParameter> Parameters => Schema;

    public async Task<ChatToolResult> ExecuteAsync(
        Dictionary<string, string> parameters,
        CancellationToken ct = default)
    {
        _logger.LogInformation("ListNumberSequencesTool executing");

        // The allowed values were already checked centrally, so a value that is present parses.
        EDocumentType? documentType = null;
        if (SettingsChatToolSupport.OptionalText(parameters, "document_type") is { } documentTypeText &&
            Enum.TryParse<EDocumentType>(documentTypeText, ignoreCase: true, out var parsed))
        {
            documentType = parsed;
        }

        var includeInactive = SettingsChatToolSupport.OptionalFlag(parameters, "include_inactive") ?? false;

        var sequences = await _numberSequenceService.GetAllSequencesAsync(documentType, includeInactive, ct);
        var formats = await _numberSequenceService.GetAllFormatsAsync(includeInactive, ct);

        var heading = documentType is null
            ? "All number sequences"
            : $"Number sequences for document type {documentType}";

        return ChatToolResult.Success(
            $"{heading}:\n" +
            SettingsChatToolSupport.FormatSequences(sequences) +
            SettingsChatToolSupport.FormatFormats(formats));
    }
}

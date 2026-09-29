using Fakvio.Contracts.Dto.Readiness;
using Microsoft.Extensions.Localization;

namespace Fakvio.UI.Shared.Components.Shared;

/// <summary>
/// Turns one <see cref="ReadinessIssueDto"/> into the sentence the user reads.
///
/// Shared by <c>ReadinessBanner</c> (issue #215) and <c>SetupChecklist</c> (issue #210): both
/// surfaces describe the very same problem, so the wording rule lives here instead of being
/// copied into each component — one place to change when a code gains a nicer sentence.
/// </summary>
public static class ReadinessIssueText
{
    /// <summary>
    /// Localized description of the issue, with the issuer name appended when the problem
    /// belongs to one specific company (a tenant may have several issuers).
    /// </summary>
    public static string Describe(IStringLocalizer<SharedResource> localizer, ReadinessIssueDto issue)
    {
        // The code is a localization key, never shown raw — an unknown code (older UI against a
        // newer API) falls back to a generic sentence instead of leaking "ISSUER_FOO" to the user.
        var text = localizer[$"Readiness_Code_{issue.Code}"];
        var message = text.ResourceNotFound ? localizer["Readiness_Code_Unknown"].Value : text.Value;

        return string.IsNullOrWhiteSpace(issue.IssuerName)
            ? message
            : $"{message} ({issue.IssuerName})";
    }

    /// <summary>
    /// Localized, comma-separated list of <see cref="ReadinessIssueDto.MissingFields"/>, or an
    /// empty string when the issue names no field.
    ///
    /// The API sends machine names ("CreditNote", "PostalCode") — fine for logs and the AI
    /// model, wrong for a user who reads Czech. Two key families are needed because the list
    /// means different things per code:
    ///  - NUMBER_SEQUENCE_MISSING lists document types (EDocumentType names), which already
    ///    have labels under "Template_DocumentType{Type}" (the same ones the document-type
    ///    selects show),
    ///  - every other rule lists entity property names → "Readiness_Field_{Name}".
    /// </summary>
    public static string DescribeMissingFields(IStringLocalizer<SharedResource> localizer, ReadinessIssueDto issue)
    {
        var keyPrefix = issue.Code == ReadinessCodes.NumberSequenceMissing
            ? "Template_DocumentType"
            : "Readiness_Field_";

        return string.Join(", ", issue.MissingFields.Select(field =>
        {
            var text = localizer[keyPrefix + field];
            // A field the UI has no label for yet (newer API than UI): the raw name is still
            // more useful to the user than dropping the item from the list.
            return text.ResourceNotFound ? field : text.Value;
        }));
    }
}

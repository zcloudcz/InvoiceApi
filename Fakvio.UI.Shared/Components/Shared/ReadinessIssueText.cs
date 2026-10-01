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
    /// <summary>API field identifiers remain stable; presentation uses readable translated labels.</summary>
    public static string Fields(IStringLocalizer<SharedResource> localizer, IEnumerable<string> fields)
        => string.Join(", ", fields.Select(field =>
        {
            var label = localizer[$"Readiness_Field_{field}"];
            return label.ResourceNotFound ? localizer["Readiness_Field_Unknown"].Value : label.Value;
        }));
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
}

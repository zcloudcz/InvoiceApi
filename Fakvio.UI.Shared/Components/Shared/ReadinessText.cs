using Fakvio.Contracts.Dto.Readiness;
using Microsoft.Extensions.Localization;

namespace Fakvio.UI.Shared.Components.Shared;

/// <summary>
/// Turns a readiness finding into the sentence the user reads.
///
/// Extracted from <see cref="ReadinessBanner"/> when the conversational onboarding
/// (issue #214) became a second consumer: the banner and the assistant's welcome must
/// describe the very same finding with the very same words, and two copies of the
/// "code → localization key, with a fallback" rule would drift on the first new code.
///
/// A pure function on purpose — no component, no injected state — so it is unit testable
/// without rendering anything.
/// </summary>
public static class ReadinessText
{
    /// <summary>
    /// Human-readable text for one issue: the localized message for its code, plus the issuer
    /// name when the problem belongs to a specific company (a tenant may have several).
    /// </summary>
    /// <param name="issue">The finding as the API returned it.</param>
    /// <param name="localizer">Shared UI localizer — supplies both languages of the app.</param>
    public static string Describe(ReadinessIssueDto issue, IStringLocalizer<SharedResource> localizer)
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

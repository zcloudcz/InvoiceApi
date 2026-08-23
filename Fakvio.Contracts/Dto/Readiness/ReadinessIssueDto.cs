using Fakvio.Domain.Enums;

namespace Fakvio.Contracts.Dto.Readiness;

/// <summary>
/// One missing / unfinished tenant setting.
///
/// The shape generalizes the existing EPO precedent (HTTP 400 with
/// <c>{ code, missingFields }</c>) and adds two things the UI needs to be helpful:
/// a severity (is the user blocked right now?) and a fix route (where to go to fix it).
/// </summary>
public class ReadinessIssueDto
{
    /// <summary>
    /// Machine-readable code — one of <see cref="ReadinessCodes"/>.
    /// The UI uses it as a localization key; never show it raw to the user.
    /// </summary>
    public string Code { get; set; } = string.Empty;

    /// <summary>
    /// Blocking = business operations must be refused. Warning = only informational.
    /// </summary>
    public EReadinessSeverity Severity { get; set; }

    /// <summary>
    /// Names of the concrete fields that are empty, e.g. "Street", "RegistrationNumber".
    /// Same meaning as <c>EpoHeaderIncompleteException.MissingFields</c>.
    /// </summary>
    public List<string> MissingFields { get; set; } = new();

    /// <summary>
    /// Relative UI route where the user fixes this, e.g. "/my-company".
    /// The API never builds absolute URLs — the UI just renders this as a link.
    /// </summary>
    public string FixRoute { get; set; } = string.Empty;

    /// <summary>
    /// Which issuer the problem belongs to. Null for tenant-wide problems
    /// (number sequences, EPO header) that are not tied to a single issuer.
    /// </summary>
    public long? IssuerId { get; set; }

    /// <summary>
    /// Company name of <see cref="IssuerId"/> — so the UI can say which of several
    /// issuers is incomplete without a second round-trip. Null for tenant-wide problems.
    /// </summary>
    public string? IssuerName { get; set; }
}

namespace Fakvio.Application.Exceptions;

/// <summary>
/// Thrown when one or more required EPO header fields are missing from
/// <see cref="Fakvio.Domain.Entities.CompanySystemSettings"/>.
///
/// The controller catches this exception and converts it to HTTP 400 Bad Request
/// with machine-readable code <c>EPO_HEADER_INCOMPLETE</c> and a list of the
/// missing field names so the UI can navigate the user to the settings screen.
/// </summary>
public sealed class EpoHeaderIncompleteException : Exception
{
    /// <summary>
    /// Names of the required EPO header fields that are null / not configured.
    /// Each name corresponds to a <see cref="Fakvio.Domain.Entities.CompanySystemSettings"/>
    /// property, e.g. "EpoTaxOfficeCode", "EpoTaxOfficeBranchCode".
    /// </summary>
    public IReadOnlyList<string> MissingFields { get; }

    /// <param name="missingFields">Non-empty list of missing required field names.</param>
    public EpoHeaderIncompleteException(IReadOnlyList<string> missingFields)
        : base(BuildMessage(missingFields))
    {
        MissingFields = missingFields;
    }

    private static string BuildMessage(IReadOnlyList<string> missingFields)
        => $"EPO header is incomplete. Missing required field(s): {string.Join(", ", missingFields)}.";
}

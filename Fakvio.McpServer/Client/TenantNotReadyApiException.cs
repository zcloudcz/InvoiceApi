using Fakvio.Contracts.Dto.Readiness;

namespace Fakvio.McpServer.Client;

/// <summary>
/// Thrown when the API refuses a write with the <c>TENANT_NOT_READY</c> 400 body (#342).
///
/// <see cref="FakvioApiClient"/> cannot reuse <c>Fakvio.Application.Exceptions.TenantNotReadyException</c>
/// — the MCP server deliberately has no reference to <c>Fakvio.Application</c> (it only talks to
/// the API over REST, see DEVGUIDE §4.9) — so this is a lightweight mirror carrying the same
/// structured payload, letting an MCP tool return it instead of a flattened error string.
/// </summary>
public sealed class TenantNotReadyApiException : Exception
{
    /// <summary>Same value as <c>TenantNotReadyException.ErrorCode</c> on the API side.</summary>
    public const string ErrorCode = "TENANT_NOT_READY";

    /// <summary>Flat, de-duplicated list of the field names from all <see cref="Issues"/>.</summary>
    public IReadOnlyList<string> MissingFields { get; }

    /// <summary>The blocking issues that caused the refusal.</summary>
    public IReadOnlyList<ReadinessIssueDto> Issues { get; }

    public TenantNotReadyApiException(
        string message, IReadOnlyList<string> missingFields, IReadOnlyList<ReadinessIssueDto> issues)
        : base(message)
    {
        MissingFields = missingFields;
        Issues = issues;
    }
}

using Fakvio.Contracts.Dto.Readiness;

namespace Fakvio.Application.Exceptions;

/// <summary>
/// Thrown when a business operation is refused because the tenant has unfinished
/// mandatory settings (missing issuer address, no bank account, no number sequence, …).
///
/// This generalizes <see cref="EpoHeaderIncompleteException"/>: a controller catches it and
/// returns HTTP 400 with the very same shape the EPO export already uses —
/// <c>{ code, message, missingFields }</c> — plus <see cref="Issues"/> so the UI can render
/// a "fix it here" link per problem instead of a flat list of field names.
/// </summary>
public sealed class TenantNotReadyException : Exception
{
    /// <summary>Machine-readable error code returned to the client.</summary>
    public const string ErrorCode = "TENANT_NOT_READY";

    /// <inheritdoc cref="ErrorCode"/>
    public string Code => ErrorCode;

    /// <summary>
    /// Flat, de-duplicated list of the field names from all <see cref="Issues"/>.
    /// Kept for shape-compatibility with the EPO precedent.
    /// </summary>
    public IReadOnlyList<string> MissingFields { get; }

    /// <summary>
    /// The blocking issues that caused the refusal (never empty).
    /// </summary>
    public IReadOnlyList<ReadinessIssueDto> Issues { get; }

    /// <param name="blockingIssues">
    /// Non-empty list of blocking readiness issues. An empty list means the caller decided
    /// the tenant is not ready without knowing why — that is a bug, so we fail loudly.
    /// </param>
    public TenantNotReadyException(IReadOnlyList<ReadinessIssueDto> blockingIssues)
        : base(BuildMessage(blockingIssues))
    {
        if (blockingIssues.Count == 0)
            throw new ArgumentException(
                "TenantNotReadyException requires at least one blocking issue.",
                nameof(blockingIssues));

        Issues = blockingIssues;
        MissingFields = blockingIssues
            .SelectMany(i => i.MissingFields)
            .Distinct()
            .ToList();
    }

    private static string BuildMessage(IReadOnlyList<ReadinessIssueDto> blockingIssues)
        => $"Tenant is not ready. Unresolved blocking issue(s): {string.Join(", ", blockingIssues.Select(i => i.Code))}.";
}

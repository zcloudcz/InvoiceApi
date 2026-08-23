using Fakvio.Domain.Enums;

namespace Fakvio.Contracts.Dto.Readiness;

/// <summary>
/// Result of a tenant readiness check — everything that is missing before the tenant
/// can invoice safely, in one response.
/// </summary>
public class ReadinessReportDto
{
    /// <summary>
    /// All findings, blocking and warning alike, in the order the rules ran.
    /// Empty list = nothing to fix.
    /// </summary>
    public List<ReadinessIssueDto> Issues { get; set; } = new();

    /// <summary>
    /// True when no <see cref="EReadinessSeverity.Blocking"/> issue is present.
    /// Computed (not stored) so it can never contradict <see cref="Issues"/>.
    /// </summary>
    public bool IsReady => !Issues.Any(i => i.Severity == EReadinessSeverity.Blocking);
}

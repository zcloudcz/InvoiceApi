using System.Text;
using Fakvio.Contracts.Dto.Readiness;
using Fakvio.Domain.Enums;

namespace Fakvio.Infrastructure.Service.ChatTools;

/// <summary>
/// Renders a single <see cref="ReadinessIssueDto"/> as the text block the AI model reads.
///
/// Shared by every chat tool that reports a readiness issue — <see cref="GetReadinessTool"/>
/// (the full report) and <see cref="CompleteInvoiceTool"/> (the refusal when issuing hits a
/// blocking issue, #342) — so the two surfaces never describe the same problem differently.
/// </summary>
internal static class ReadinessIssueFormatter
{
    /// <summary>
    /// Appends one issue: machine code, which issuer it belongs to, the empty fields and the
    /// fix route. The fix route is what lets the assistant follow up with a link to the page
    /// that resolves the problem.
    /// </summary>
    public static void AppendIssue(StringBuilder sb, ReadinessIssueDto issue)
    {
        var severity = issue.Severity == EReadinessSeverity.Blocking ? "BLOCKING" : "WARNING";
        var issuer = issue.IssuerName is null ? string.Empty : $" (issuer: {issue.IssuerName})";

        sb.AppendLine($"[{severity}] {issue.Code}{issuer}");
        sb.AppendLine($"  Missing: {string.Join(", ", issue.MissingFields)}");
        sb.AppendLine($"  Fix at: {issue.FixRoute}");
        sb.AppendLine();
    }
}

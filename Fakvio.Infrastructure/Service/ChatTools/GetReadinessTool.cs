using System.Text;
using Fakvio.Application.Service;
using Fakvio.Contracts.Dto.Readiness;
using Fakvio.Domain.Enums;
using Microsoft.Extensions.Logging;

namespace Fakvio.Infrastructure.Service.ChatTools;

/// <summary>
/// Chat tool that answers "can this tenant issue documents yet, and what is still missing?".
///
/// Typical usage:
///   "Můžu už fakturovat?" / "Co mi ještě chybí v nastavení?" / "Am I ready to invoice?"
///
/// A thin wrapper over <see cref="ITenantReadinessService"/> — every rule lives there
/// (DEVGUIDE §4.12), so the chat assistant, the readiness banner and the invoice gate
/// cannot answer the same question differently.
///
/// Junior note: read-only tool. It changes nothing, so it is safe to call repeatedly and
/// needs no confirmation step. Every issue is rendered together with its fix route, which
/// is what lets the assistant follow up with <c>navigate</c> to the page that fixes it.
/// </summary>
public class GetReadinessTool : IChatTool
{
    private readonly ITenantReadinessService _readinessService;
    private readonly ILogger<GetReadinessTool> _logger;

    public GetReadinessTool(
        ITenantReadinessService readinessService,
        ILogger<GetReadinessTool> logger)
    {
        _readinessService = readinessService;
        _logger = logger;
    }

    public string ToolName => "get_readiness";

    public string Description =>
        "Checks whether the company setup is complete enough to issue invoices. Returns every " +
        "missing setting with its severity (blocking or warning), the empty fields and the page " +
        "where the user fixes it. Read-only — use it when the user asks what is still missing, " +
        "or when an invoice was refused because the setup is incomplete.";

    /// <summary>
    /// No parameters: the report always covers the whole tenant. Narrowing it to one issuer
    /// would need a database ID the model has no way of knowing, and every issuer-bound issue
    /// already names its issuer.
    /// </summary>
    public IReadOnlyList<ChatToolParameter> Parameters => [];

    public async Task<ChatToolResult> ExecuteAsync(
        Dictionary<string, string> parameters,
        CancellationToken ct = default)
    {
        // Unfiltered report: no issuerId, no documentType — the user is asking about the
        // whole setup, not about one document they are trying to issue right now.
        var report = await _readinessService.GetReportAsync(ct: ct);

        _logger.LogInformation(
            "GetReadinessTool: IsReady={IsReady}, {IssueCount} issue(s)",
            report.IsReady, report.Issues.Count);

        return ChatToolResult.Success(Format(report));
    }

    /// <summary>
    /// Renders the report as the text block the model reads: a headline that says whether
    /// invoicing is possible, then one paragraph per issue.
    /// </summary>
    private static string Format(ReadinessReportDto report)
    {
        if (report.Issues.Count == 0)
            return "Company setup is complete — nothing is missing, invoices can be issued.";

        var blocking = report.Issues.Count(i => i.Severity == EReadinessSeverity.Blocking);
        var warnings = report.Issues.Count - blocking;

        var sb = new StringBuilder();
        sb.AppendLine(report.IsReady
            ? $"Company setup is usable, but {warnings} setting(s) are still incomplete:"
            : $"Company setup is incomplete — {blocking} blocking issue(s), {warnings} warning(s):");
        sb.AppendLine();

        foreach (var issue in report.Issues)
            ReadinessIssueFormatter.AppendIssue(sb, issue);

        // Warnings never stop invoicing — say so, otherwise the model reports them as blockers.
        sb.Append(report.IsReady
            ? "Warnings do not block invoicing; they only affect the feature they belong to."
            : "Blocking issues must be fixed before an invoice can be completed.");

        return sb.ToString();
    }
}

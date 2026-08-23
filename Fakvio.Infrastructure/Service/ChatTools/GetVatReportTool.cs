using System.Text;
using Fakvio.Application.Service;
using Fakvio.Contracts.Dto.VatReport;
using Microsoft.Extensions.Logging;

namespace Fakvio.Infrastructure.Service.ChatTools;

/// <summary>
/// Chat tool that returns the VAT (DPH) report for a period. Read-only — it does not create
/// or submit anything, it is the same aggregation the DPH page shows.
///
/// Typical usage:
///   "Jaká je moje DPH za první čtvrtletí?"
///   "Kolik odvedu na DPH za březen 2026?"
///   "VAT report from 2026-01-01 to 2026-03-31"
///
/// The period is matched on TaxableSupplyDate (DUZP), not on the issue date — that is what
/// the tax office cares about.
/// </summary>
public class GetVatReportTool : IChatTool
{
    private readonly IVatReportService _vatReportService;
    private readonly ILogger<GetVatReportTool> _logger;

    public GetVatReportTool(
        IVatReportService vatReportService,
        ILogger<GetVatReportTool> logger)
    {
        _vatReportService = vatReportService;
        _logger = logger;
    }

    public string ToolName => "get_vat_report";

    public string Description =>
        "Get the VAT (DPH) report for a period: output VAT from issued invoices, input VAT from " +
        "received invoices, the resulting tax liability, plus revenue, expenses and profit. " +
        "The period is matched on the taxable supply date (DUZP). Read-only.";

    /// <summary>Parameter schema — static because it never changes per instance.</summary>
    private static readonly ChatToolParameter[] Schema =
    [
        new()
        {
            Name = "date_from",
            Type = ChatToolParameterType.String,
            Description = "Period start (inclusive) in YYYY-MM-DD format",
            IsRequired = true
        },
        new()
        {
            Name = "date_to",
            Type = ChatToolParameterType.String,
            Description = "Period end (inclusive) in YYYY-MM-DD format",
            IsRequired = true
        }
    ];

    public IReadOnlyList<ChatToolParameter> Parameters => Schema;

    /// <summary>
    /// Parses the period and delegates to <see cref="IVatReportService"/>.
    ///
    /// Presence of both dates is guaranteed by the central validation; their FORMAT is not
    /// (the schema can only say "string"), so an unparsable date is rejected here rather than
    /// silently turned into "today" — a VAT report for the wrong period looks perfectly
    /// plausible and would be believed.
    /// </summary>
    public async Task<ChatToolResult> ExecuteAsync(
        Dictionary<string, string> parameters,
        CancellationToken ct = default)
    {
        if (!ChatToolDates.TryParse(parameters, "date_from", out var from))
            return ChatToolResult.Failure(
                $"Invalid date_from: '{parameters.GetValueOrDefault("date_from")}'. Use YYYY-MM-DD.");

        if (!ChatToolDates.TryParse(parameters, "date_to", out var to))
            return ChatToolResult.Failure(
                $"Invalid date_to: '{parameters.GetValueOrDefault("date_to")}'. Use YYYY-MM-DD.");

        if (from > to)
            return ChatToolResult.Failure(
                $"Period start {ChatToolDates.Format(from)} is after period end {ChatToolDates.Format(to)}.");

        _logger.LogInformation("GetVatReportTool executing for period {From} — {To}",
            ChatToolDates.Format(from), ChatToolDates.Format(to));

        var report = await _vatReportService.GetReportAsync(from, to, ct);

        return ChatToolResult.Success(Format(report));
    }

    /// <summary>
    /// Renders the report as a compact text block: the totals first (that is what gets asked
    /// about), then the per-rate breakdown.
    /// </summary>
    private static string Format(VatReportDto report)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"VAT report {ChatToolDates.Format(report.PeriodFrom)} — {ChatToolDates.Format(report.PeriodTo)} " +
                      "(period matched on taxable supply date / DUZP):");
        sb.AppendLine($"  Output VAT (issued invoices): {report.TotalOutputVat:N2}");
        sb.AppendLine($"  Input VAT (received invoices): {report.TotalInputVat:N2}");
        sb.AppendLine(report.TaxLiability >= 0
            ? $"  Tax liability (to pay): {report.TaxLiability:N2}"
            : $"  Excess deduction (refund): {-report.TaxLiability:N2}");
        sb.AppendLine($"  Revenue before VAT: {report.TotalRevenue:N2}");
        sb.AppendLine($"  Expenses before VAT: {report.TotalExpenses:N2}");
        sb.AppendLine($"  Profit before VAT: {report.Profit:N2}");
        sb.AppendLine($"  Invoice counts: {report.IssuedInvoiceCount} issued, {report.ReceivedInvoiceCount} received");

        AppendBreakdown(sb, "Output VAT by rate", report.OutputVat);
        AppendBreakdown(sb, "Input VAT by rate", report.InputVat);

        return sb.ToString();
    }

    /// <summary>
    /// Appends one per-rate section, or nothing when there is nothing to break down.
    /// </summary>
    private static void AppendBreakdown(StringBuilder sb, string heading, List<VatReportLineDto> lines)
    {
        if (lines.Count == 0)
            return;

        sb.AppendLine($"  {heading}:");
        foreach (var line in lines)
        {
            sb.AppendLine(
                $"    {line.VatRatePercentage:0.##}% ({line.VatRateLabel}) | " +
                $"base: {line.BaseAmount:N2} | VAT: {line.VatAmount:N2} | items: {line.ItemCount}");
        }
    }
}

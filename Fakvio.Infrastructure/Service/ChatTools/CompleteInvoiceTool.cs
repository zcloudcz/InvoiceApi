using System.Text;
using Fakvio.Application.Exceptions;
using Fakvio.Application.Service;
using Fakvio.Contracts.Dto.Invoice;
using Fakvio.Domain.Enums;
using Microsoft.Extensions.Logging;

namespace Fakvio.Infrastructure.Service.ChatTools;

/// <summary>
/// Chat tool that issues (completes) a draft invoice — Draft → Completed, which draws the
/// document number and turns the draft into a tax document. Mirrors MCP <c>CompleteInvoice</c>.
///
/// Data-changing, so it goes through the confirm gate (<see cref="IConfirmableChatTool"/>,
/// DEVGUIDE §4.7): the first call only describes what would be issued.
/// </summary>
public class CompleteInvoiceTool : IConfirmableChatTool
{
    private readonly IInvoiceService _invoiceService;
    private readonly ILogger<CompleteInvoiceTool> _logger;

    public CompleteInvoiceTool(IInvoiceService invoiceService, ILogger<CompleteInvoiceTool> logger)
    {
        _invoiceService = invoiceService;
        _logger = logger;
    }

    public string ToolName => "complete_invoice";

    public string Description =>
        "Issue (complete) a DRAFT issued invoice: status changes from Draft to Completed and the " +
        "document number is assigned. This cannot be undone from chat. Identify the draft by ID " +
        "or document number.";

    public IReadOnlyList<ChatToolParameter> Parameters => InvoiceLookup.IdentitySchema;

    /// <summary>
    /// Describes the document that would be issued. Nothing is written.
    /// </summary>
    public async Task<ChatToolResult> BuildPreviewAsync(
        Dictionary<string, string> parameters,
        CancellationToken ct = default)
    {
        var (invoice, error) = await LoadIssuableAsync(parameters, ct);
        if (invoice is null)
            return ChatToolResult.Failure(error!);

        return ChatToolResult.Success(
            $"Will issue {InvoiceLookup.Describe(invoice)}: status Draft → Completed, " +
            "the document number is drawn from the number sequence and the document becomes " +
            "a tax record. This cannot be undone from chat.");
    }

    public async Task<ChatToolResult> ExecuteAsync(
        Dictionary<string, string> parameters,
        CancellationToken ct = default)
    {
        // Re-checked on purpose: the preview and this call are not paired, and the user may have
        // issued the draft elsewhere in between (see IConfirmableChatTool).
        var (invoice, error) = await LoadIssuableAsync(parameters, ct);
        if (invoice is null)
            return ChatToolResult.Failure(error!);

        _logger.LogInformation("CompleteInvoiceTool issuing invoice {InvoiceId}", invoice.Id);

        try
        {
            var completed = await _invoiceService.CompleteInvoiceAsync(invoice.Id, ct);
            if (completed is null)
                return ChatToolResult.Failure($"Issued invoice with ID {invoice.Id} not found.");

            return ChatToolResult.Success(
                $"Issued {completed.DocumentType} {completed.DocumentNumber ?? $"(ID {completed.Id})"} " +
                $"for {completed.ClientName}. Status is now {completed.Status}, " +
                $"due {ChatToolDates.Format(completed.DueDate)}.");
        }
        catch (TenantNotReadyException ex)
        {
            // Handled here rather than left to ChatToolExecutor's catch-all (#342) — that would
            // wrap it as "Tool execution failed: <ex.Message>", losing the fix route per issue
            // that the assistant needs to tell the user where to fix it. Same rendering as
            // GetReadinessTool, so the two do not describe the same problem differently.
            _logger.LogInformation(
                "CompleteInvoiceTool: refused by readiness gate — {Codes}",
                string.Join(", ", ex.Issues.Select(i => i.Code)));

            var sb = new StringBuilder();
            sb.AppendLine($"Cannot issue {InvoiceLookup.Describe(invoice)} — company setup is incomplete:");
            sb.AppendLine();
            foreach (var issue in ex.Issues)
                ReadinessIssueFormatter.AppendIssue(sb, issue);

            return ChatToolResult.Failure(sb.ToString().TrimEnd());
        }
    }

    /// <summary>
    /// Resolves the document and refuses anything that is not a draft, so the preview and the
    /// write agree on what "issuable" means — and the user is told why, instead of reading a
    /// raw exception from the service.
    /// </summary>
    private async Task<(InvoiceDto? Invoice, string? Error)> LoadIssuableAsync(
        Dictionary<string, string> parameters, CancellationToken ct)
    {
        var (invoice, error) = await InvoiceLookup.ResolveAsync(_invoiceService, parameters, ct);
        if (invoice is null)
            return (null, error);

        return invoice.Status == EInvoiceStatus.Draft
            ? (invoice, null)
            : (null, $"{InvoiceLookup.Describe(invoice)} is already {invoice.Status} — only a Draft can be issued.");
    }
}

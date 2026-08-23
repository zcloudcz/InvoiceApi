using System.Net.Mail;
using Fakvio.Application.Service;
using Microsoft.Extensions.Logging;

namespace Fakvio.Infrastructure.Service.ChatTools;

/// <summary>
/// Chat tool that e-mails an issued invoice to a recipient, with the PDF and ISDOC attachments
/// the application generates. Mirrors MCP <c>SendInvoiceEmail</c> and the Email action in the
/// invoice grid (available for any status).
///
/// Data-changing — an e-mail cannot be unsent — so it goes through the confirm gate
/// (<see cref="IConfirmableChatTool"/>, DEVGUIDE §4.7). The preview is what lets the user spot
/// a wrong address BEFORE the message leaves.
///
/// The recipient is required: unlike the invoice dialog in the UI, the assistant has no field
/// pre-filled from the client's billing settings, and guessing an address is exactly the kind
/// of silent decision the confirm step exists to prevent.
/// </summary>
public class SendInvoiceEmailTool : IConfirmableChatTool
{
    private const string RecipientParameter = "recipient_email";

    private readonly IInvoiceService _invoiceService;
    private readonly IEmailService _emailService;
    private readonly ILogger<SendInvoiceEmailTool> _logger;

    public SendInvoiceEmailTool(
        IInvoiceService invoiceService,
        IEmailService emailService,
        ILogger<SendInvoiceEmailTool> logger)
    {
        _invoiceService = invoiceService;
        _emailService = emailService;
        _logger = logger;
    }

    public string ToolName => "send_invoice_email";

    public string Description =>
        "Send an issued invoice by e-mail with the PDF and ISDOC attachments. " +
        "Identify the invoice by ID or document number and give the recipient address.";

    /// <summary>
    /// The shared identity parameters plus the recipient. Static readonly — the schema never
    /// changes per instance.
    /// </summary>
    private static readonly ChatToolParameter[] Schema =
    [
        .. InvoiceLookup.IdentitySchema,
        new()
        {
            Name = RecipientParameter,
            Type = ChatToolParameterType.String,
            Description = "Recipient e-mail address",
            IsRequired = true
        }
    ];

    public IReadOnlyList<ChatToolParameter> Parameters => Schema;

    public async Task<ChatToolResult> BuildPreviewAsync(
        Dictionary<string, string> parameters,
        CancellationToken ct = default)
    {
        var (invoice, recipient, error) = await LoadSendableAsync(parameters, ct);
        if (invoice is null)
            return ChatToolResult.Failure(error!);

        var resend = invoice.IsSentByEmail
            ? $" It was already sent on {ChatToolDates.Format(invoice.LastSentByEmailAt)} — this would send it again."
            : string.Empty;

        return ChatToolResult.Success(
            $"Will e-mail {InvoiceLookup.Describe(invoice)} to {recipient}, " +
            $"with the PDF and ISDOC attachments.{resend}");
    }

    public async Task<ChatToolResult> ExecuteAsync(
        Dictionary<string, string> parameters,
        CancellationToken ct = default)
    {
        // Re-checked: the preview and this call are not paired (see IConfirmableChatTool).
        var (invoice, recipient, error) = await LoadSendableAsync(parameters, ct);
        if (invoice is null)
            return ChatToolResult.Failure(error!);

        _logger.LogInformation("SendInvoiceEmailTool sending invoice {InvoiceId}", invoice.Id);

        await _emailService.SendInvoiceEmailAsync(invoice.Id, recipient!, ct);

        return ChatToolResult.Success(
            $"{InvoiceLookup.Describe(invoice)} was sent to {recipient}.");
    }

    /// <summary>
    /// Resolves the document and the recipient address. The address is validated here because
    /// the schema can only say "string" — and an unsendable address must fail before the PDF
    /// is generated, not inside the SMTP client.
    /// </summary>
    private async Task<(Contracts.Dto.Invoice.InvoiceDto? Invoice, string? Recipient, string? Error)> LoadSendableAsync(
        Dictionary<string, string> parameters, CancellationToken ct)
    {
        // Required by the schema, so it is present — but the executor dispatches the raw value.
        var recipient = parameters[RecipientParameter].Trim();

        if (!MailAddress.TryCreate(recipient, out _))
            return (null, null, $"Invalid recipient_email: '{recipient}' is not a valid e-mail address.");

        var (invoice, error) = await InvoiceLookup.ResolveAsync(_invoiceService, parameters, ct);

        return invoice is null ? (null, null, error) : (invoice, recipient, null);
    }
}

using System.Text;
using Fakvio.Application.Service;
using Fakvio.Contracts.Dto.PaymentMatching;
using Fakvio.Domain.Enums;
using Microsoft.Extensions.Logging;

namespace Fakvio.Infrastructure.Service.ChatTools;

/// <summary>
/// Chat tool that returns the full detail of one bank payment — issue #227.
///
/// Typical usage:
///   "Ukaž mi detail platby 42."
///   "Ke které faktuře patří ta platba?"
///
/// Read-only, like <see cref="ListPaymentsTool"/>. The payment is addressed by the ID that
/// list_payments prints, which is the only identifier the model can know — a bank transaction
/// has no user-facing number.
/// </summary>
public class GetPaymentTool : IChatTool
{
    private readonly IBankTransactionQueryService _bankTransactionQueryService;
    private readonly ILogger<GetPaymentTool> _logger;

    public GetPaymentTool(
        IBankTransactionQueryService bankTransactionQueryService,
        ILogger<GetPaymentTool> logger)
    {
        _bankTransactionQueryService = bankTransactionQueryService;
        _logger = logger;
    }

    public string ToolName => "get_payment";

    public string Description =>
        "Get the full detail of one bank payment: amount, date, counterparty, symbols, message, " +
        "how it was imported, and which invoices it has been matched to. Identify the payment by " +
        "the ID from a list_payments result. Read-only.";

    /// <summary>
    /// Parameter schema — static because it never changes per instance.
    /// One required ID; presence and the numeric format are validated centrally by the executor.
    /// </summary>
    private static readonly ChatToolParameter[] Schema =
    [
        new()
        {
            Name = "id",
            Type = ChatToolParameterType.Integer,
            Description = "Internal database ID of the payment (from a list_payments result)",
            IsRequired = true
        }
    ];

    public IReadOnlyList<ChatToolParameter> Parameters => Schema;

    public async Task<ChatToolResult> ExecuteAsync(
        Dictionary<string, string> parameters,
        CancellationToken ct = default)
    {
        _logger.LogInformation("GetPaymentTool executing with parameters: {Params}",
            string.Join(", ", parameters.Select(pair => $"{pair.Key}={pair.Value}")));

        // The schema declares 'id' as a required integer and the executor validates that before
        // the tool runs, so this only guards direct calls (tests, future callers).
        if (!long.TryParse(parameters.GetValueOrDefault("id")?.Trim(), out var id))
            return ChatToolResult.Failure("Invalid id parameter — must be a numeric payment ID from list_payments.");

        var payment = await _bankTransactionQueryService.GetAsync(id, ct);

        return payment is null
            ? ChatToolResult.Failure($"Payment with ID {id} not found.")
            : ChatToolResult.Success(Format(payment));
    }

    /// <summary>
    /// Renders the payment as a text block — one fact per line, because this text is the
    /// model's only source for the answer and anything missing gets invented instead.
    /// Optional fields the bank did not fill in are skipped rather than printed as "(none)",
    /// so the block stays readable for the common case of a plain incoming transfer.
    /// </summary>
    private static string Format(BankTransactionDto payment)
    {
        var sb = new StringBuilder();
        sb.AppendLine("Payment detail:");
        sb.AppendLine($"  Payment ID: {payment.Id}");
        sb.AppendLine($"  Date: {ChatToolDates.Format(payment.TransactionDate)}");
        sb.AppendLine($"  Amount: {PaymentChatToolSupport.FormatSignedAmount(payment)}" +
                      $" ({(payment.Direction == EPaymentDirection.Outgoing ? "money sent" : "money received")})");
        sb.AppendLine($"  Our bank account: {payment.BankAccountLabel ?? $"ID {payment.BankAccountId}"}");
        sb.AppendLine($"  Counterparty: {payment.CounterpartyName ?? "(unknown)"}");

        AppendIfPresent(sb, "Counterparty account", payment.CounterpartyAccount);
        AppendIfPresent(sb, "Variable symbol", payment.VariableSymbol);
        AppendIfPresent(sb, "Constant symbol", payment.ConstantSymbol);
        AppendIfPresent(sb, "Specific symbol", payment.SpecificSymbol);
        AppendIfPresent(sb, "Message", payment.Message);
        AppendIfPresent(sb, "Bank transaction code", payment.TransactionCode);

        sb.AppendLine($"  Imported from: {payment.ImportSource}");
        sb.AppendLine($"  Matching status: {payment.MatchStatus}");

        if (payment.MatchedInvoiceNumbers.Count > 0)
        {
            sb.AppendLine($"  Matched to invoices: {string.Join(", ", payment.MatchedInvoiceNumbers)}");
            sb.AppendLine($"  Matched amount: {PaymentChatToolSupport.FormatAmount(payment.MatchedTotal)} {payment.CurrencyCode}" +
                          $" out of {PaymentChatToolSupport.FormatAmount(payment.Amount)} {payment.CurrencyCode}");
        }

        if (payment.RecognizedCounterpartyLabel is { Length: > 0 } label)
            sb.AppendLine($"  Recognized counterparty: {label}" +
                          $"{(payment.RecognizedCategory is { } category ? $" (category: {category})" : string.Empty)}");

        return sb.ToString();
    }

    /// <summary>Writes one optional line, or nothing when the bank did not supply the value.</summary>
    private static void AppendIfPresent(StringBuilder sb, string label, string? value)
    {
        if (!string.IsNullOrWhiteSpace(value))
            sb.AppendLine($"  {label}: {value}");
    }
}

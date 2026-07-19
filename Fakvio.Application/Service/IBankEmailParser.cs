using Fakvio.Domain.Enums;

namespace Fakvio.Application.Service;

/// <summary>
/// Raw input to the bank email parser. We pass headers + both bodies so the AI
/// can see From/Subject (which often already hints at the bank) and fall back
/// from HTML to plain text as needed.
/// </summary>
public record BankEmailInput(
    string From,
    string Subject,
    string? TextBody,
    string? HtmlBody,
    DateTime ReceivedAt);

/// <summary>
/// Structured extraction produced by <see cref="IBankEmailParser"/>.
/// </summary>
public record BankEmailParsed(
    decimal Amount,
    string CurrencyCode,
    EPaymentDirection Direction,
    DateTime TransactionDate,
    string? VariableSymbol,
    string? ConstantSymbol,
    string? SpecificSymbol,
    string? CounterpartyAccount,
    string? CounterpartyName,
    string? Message,
    string? TransactionCode,
    decimal Confidence,
    string ModelUsed);

/// <summary>
/// Parses a bank notification email into structured transaction data.
/// Returns null when the email cannot be interpreted as a payment notification
/// — the caller stores the email with ParseStatus=NeedsReview so the user can
/// resolve it manually.
/// </summary>
public interface IBankEmailParser
{
    /// <summary>
    /// Attempts to parse the email. Returns null on any failure (timeout, AI
    /// unavailable, unusable JSON, low confidence, not-a-payment) so the caller
    /// can gracefully record the failure without try/catch.
    /// </summary>
    Task<BankEmailParsed?> ParseAsync(
        BankEmailInput input,
        long? companyId,
        CancellationToken ct = default);
}

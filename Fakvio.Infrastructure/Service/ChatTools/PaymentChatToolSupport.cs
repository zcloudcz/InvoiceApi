using System.Globalization;
using Fakvio.Contracts.Dto.PaymentMatching;
using Fakvio.Domain.Enums;

namespace Fakvio.Infrastructure.Service.ChatTools;

/// <summary>
/// Shared wording for the two payment chat tools — <see cref="ListPaymentsTool"/> and
/// <see cref="GetPaymentTool"/> (issue #227).
///
/// It exists so the same payment reads the same way in a list row and in its detail. The model
/// only ever sees this text, and an amount that loses its sign between the two would turn an
/// outgoing payment into income in the answer.
/// </summary>
internal static class PaymentChatToolSupport
{
    /// <summary>
    /// The amount with an explicit sign and its currency: <c>+12 500.00 CZK</c> for money
    /// received, <c>-12 500.00 CZK</c> for money sent.
    ///
    /// The sign comes from <see cref="EPaymentDirection"/>, not from the stored number — the
    /// amount column holds an absolute value, so direction is the only thing that says which
    /// way the money went.
    ///
    /// InvariantCulture on purpose: the same payment must read identically no matter what
    /// culture the API process happens to run under.
    /// </summary>
    public static string FormatSignedAmount(BankTransactionDto payment)
    {
        var sign = payment.Direction == EPaymentDirection.Outgoing ? "-" : "+";
        return $"{sign}{payment.Amount.ToString("N2", CultureInfo.InvariantCulture)} {payment.CurrencyCode}";
    }

    /// <summary>
    /// Appends what the payment has been paired with, but only when there is something to say.
    /// Without it, a row reading "PartiallyMatched" gives the model no way to answer the
    /// obvious follow-up ("with which invoice?").
    /// </summary>
    public static string FormatMatchSuffix(BankTransactionDto payment)
    {
        if (payment.MatchedInvoiceNumbers.Count > 0)
            return $" | matched to: {string.Join(", ", payment.MatchedInvoiceNumbers)}";

        return payment.RecognizedCounterpartyLabel is { Length: > 0 } label
            ? $" | recognized as: {label}"
            : string.Empty;
    }

    /// <summary>Formats a money amount for the text block handed back to the model.</summary>
    public static string FormatAmount(decimal amount)
        => amount.ToString("N2", CultureInfo.InvariantCulture);
}

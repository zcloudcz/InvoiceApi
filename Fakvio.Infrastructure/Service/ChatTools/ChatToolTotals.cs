namespace Fakvio.Infrastructure.Service.ChatTools;

/// <summary>
/// Page-total formatting shared by the reporting list tools (<see cref="ListInvoicesTool"/>,
/// <see cref="ListReceivedInvoicesTool"/>).
///
/// Junior note on why this exists: summing <c>TotalWithVat</c> across every row on a page
/// only makes sense when every row is in the same currency. A tenant invoicing in both CZK
/// and EUR would otherwise get "12100 + 500 = 12600" — a number with no unit and no
/// real-world meaning (issue #269). Grouping by currency keeps each total honest, and a
/// single-currency page still reads as one line, just now with the currency code the
/// per-row lines already carry.
/// </summary>
internal static class ChatToolTotals
{
    /// <summary>
    /// Sums <paramref name="amount"/> per distinct <paramref name="currency"/> and formats
    /// each group as "12 100,00 CZK", joined by "; ". Ordered by currency code so the output
    /// is stable across runs instead of depending on row order.
    /// </summary>
    public static string FormatPageTotal<T>(
        IEnumerable<T> items,
        Func<T, decimal> amount,
        Func<T, string> currency)
        => string.Join("; ", items
            .GroupBy(currency)
            .OrderBy(group => group.Key, StringComparer.Ordinal)
            .Select(group => $"{group.Sum(amount):N2} {group.Key}"));
}

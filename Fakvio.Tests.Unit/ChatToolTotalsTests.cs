using Fakvio.Infrastructure.Service.ChatTools;
using Shouldly;

namespace Fakvio.Tests.Unit;

/// <summary>
/// Unit tests for <see cref="ChatToolTotals"/> — the page-total grouping shared by
/// ListInvoicesTool and ListReceivedInvoicesTool (issue #269). The tool-level tests
/// (ReportingChatToolTests, ReceivedInvoiceChatToolTests) cover what each tool DOES with the
/// formatted line; these cover the grouping/ordering/formatting rules of the helper itself,
/// since both tools share exactly this code path (AC #4 — the two tools must never disagree).
///
/// Junior note: <c>:N2</c> uses the current culture, so a hardcoded "12 100,00" would pass on
/// a Czech machine and fail on a build agent running en-US. Expected strings below build the
/// same way the production code does — with the same <c>:N2</c> interpolation at test-run
/// time — so the assertion pins format, ordering and separator without pinning a culture.
/// </summary>
public class ChatToolTotalsTests
{
    private static readonly (decimal Amount, string Currency)[] NoItems = [];

    /// <summary>
    /// Two rows in the same currency collapse into a single "sum currency" group — the
    /// no-mixed-currency case that must keep reading exactly like it did before #269.
    /// </summary>
    [Fact]
    public void SingleCurrency_SumsAllRowsIntoOneGroup()
    {
        (decimal Amount, string Currency)[] items = [(12100m, "CZK"), (5000m, "CZK")];

        var result = ChatToolTotals.FormatPageTotal(items, i => i.Amount, i => i.Currency);

        result.ShouldBe($"{17100m:N2} CZK");
    }

    /// <summary>
    /// The bug this issue fixes: a mixed-currency page must never collapse into one summed
    /// number (12100 + 500 has no unit and no real-world meaning) — each currency keeps its
    /// own sum, joined by "; ".
    /// </summary>
    [Fact]
    public void MixedCurrencies_KeepsEachCurrencysSumSeparate()
    {
        (decimal Amount, string Currency)[] items = [(12100m, "CZK"), (500m, "EUR")];

        var result = ChatToolTotals.FormatPageTotal(items, i => i.Amount, i => i.Currency);

        result.ShouldBe($"{12100m:N2} CZK; {500m:N2} EUR");
    }

    /// <summary>
    /// Output order is by currency code, not by row order — otherwise the same page total
    /// would read differently depending on which currency happened to be listed first in the
    /// underlying query result, which is not a guarantee EF gives us.
    /// </summary>
    [Fact]
    public void MixedCurrencies_OrdersGroupsByCurrencyCodeRegardlessOfRowOrder()
    {
        (decimal Amount, string Currency)[] itemsInReverseAlphabeticalOrder =
            [(500m, "USD"), (300m, "EUR"), (12100m, "CZK")];

        var result = ChatToolTotals.FormatPageTotal(
            itemsInReverseAlphabeticalOrder, i => i.Amount, i => i.Currency);

        result.ShouldBe($"{12100m:N2} CZK; {300m:N2} EUR; {500m:N2} USD");
    }

    /// <summary>
    /// A single row is also a "single-currency group" — same code path as two same-currency
    /// rows, just with a sum of one addend. No separator, since there is only one group.
    /// </summary>
    [Fact]
    public void SingleItem_FormatsAsOneGroupWithNoSeparator()
    {
        (decimal Amount, string Currency)[] items = [(12100m, "CZK")];

        var result = ChatToolTotals.FormatPageTotal(items, i => i.Amount, i => i.Currency);

        result.ShouldBe($"{12100m:N2} CZK");
        result.ShouldNotContain(";");
    }

    /// <summary>
    /// An empty page has no groups to join — callers guard against this (both tools return
    /// early with "No records match" before reaching FormatPageTotal), but the helper itself
    /// should not throw on the input it is handed if that guard is ever dropped.
    /// </summary>
    [Fact]
    public void NoItems_ReturnsEmptyString()
    {
        var result = ChatToolTotals.FormatPageTotal(NoItems, i => i.Amount, i => i.Currency);

        result.ShouldBe(string.Empty);
    }
}

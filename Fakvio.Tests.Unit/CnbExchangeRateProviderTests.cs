using Fakvio.Infrastructure.Service;
using Shouldly;

namespace Fakvio.Tests.Unit;

/// <summary>
/// Unit tests for <see cref="CnbExchangeRateProvider.ParseCnbText"/>.
/// All tests are pure parsing — no HTTP calls, no database.
///
/// CNB format (as of 2025):
///   Line 0: "DD.MM.YYYY #N"
///   Line 1: "country|currency_name|amount|code|rate"  (column header)
///   Line 2+: one rate per line
/// </summary>
public class CnbExchangeRateProviderTests
{
    // ─── Happy path ───────────────────────────────────────────────────────────

    [Fact]
    public void ParseCnbText_StandardInput_ReturnsCorrectRates()
    {
        // Arrange — valid CNB format with >= 5 currencies (parser minimum)
        var cnbText = BuildSampleCnbResponse("02.05.2025", new[]
        {
            ("EMU", "euro", 1, "EUR", "25,255"),
            ("USA", "dolar", 1, "USD", "23,140"),
            ("Velká Británie", "libra", 1, "GBP", "29,450"),
            ("Polsko", "zlotý", 1, "PLN", "5,890"),
            ("Švýcarsko", "frank", 1, "CHF", "26,120"),
        });

        // Act
        var rates = CnbExchangeRateProvider.ParseCnbText(cnbText, null);

        // Assert — EUR and USD parsed correctly
        rates.Count.ShouldBe(5);
        rates.ShouldContain(r => r.CurrencyCode == "EUR" && r.Rate == 25.255m);
        rates.ShouldContain(r => r.CurrencyCode == "USD" && r.Rate == 23.140m);
    }

    [Fact]
    public void ParseCnbText_FullSample_ParsesAllCurrenciesCorrectly()
    {
        // Arrange — realistic CNB response with > 5 currencies
        var cnbText = BuildSampleCnbResponse("02.05.2025", new[]
        {
            ("EMU", "euro", 1, "EUR", "25,255"),
            ("USA", "dolar", 1, "USD", "23,140"),
            ("Velká Británie", "libra", 1, "GBP", "29,450"),
            ("Polsko", "zlotý", 1, "PLN", "5,890"),
            ("Švýcarsko", "frank", 1, "CHF", "26,120"),
            ("Japonsko", "jen", 100, "JPY", "15,123"),
        });

        // Act
        var rates = CnbExchangeRateProvider.ParseCnbText(cnbText, null);

        // Assert
        rates.ShouldNotBeNull();
        rates.Count.ShouldBe(6);

        var eur = rates.First(r => r.CurrencyCode == "EUR");
        eur.Rate.ShouldBe(25.255m);
        eur.Amount.ShouldBe(1);
        eur.Source.ShouldBe("CNB");
        eur.ValidFrom.ShouldBe(new DateOnly(2025, 5, 2));

        // JPY has Amount = 100 (CNB quotes per 100 yen)
        var jpy = rates.First(r => r.CurrencyCode == "JPY");
        jpy.Amount.ShouldBe(100);
        jpy.Rate.ShouldBe(15.123m);
    }

    [Fact]
    public void ParseCnbText_CrlfLineEndings_ParsedCorrectly()
    {
        // Arrange — Windows line endings (\r\n)
        var lines = new[]
        {
            "02.05.2025 #85",
            "země|měna|množství|kód|kurz",
            "EMU|euro|1|EUR|25,255",
            "USA|dolar|1|USD|23,140",
            "Velká Británie|libra|1|GBP|29,450",
            "Polsko|zlotý|1|PLN|5,890",
            "Švýcarsko|frank|1|CHF|26,120",
        };
        var cnbText = string.Join("\r\n", lines);

        // Act
        var rates = CnbExchangeRateProvider.ParseCnbText(cnbText, null);

        // Assert — \r\n handled transparently
        rates.Count.ShouldBe(5);
        rates.ShouldContain(r => r.CurrencyCode == "EUR");
    }

    [Fact]
    public void ParseCnbText_Weekend_UsesPublishedDateFromResponse()
    {
        // CNB publishes Friday's rates when queried on a Saturday/Sunday.
        // The ValidFrom in the parsed records must match the header date (Friday),
        // not the requested date (Saturday).
        var cnbText = BuildSampleCnbResponse("02.05.2025", new[]  // Friday
        {
            ("EMU", "euro", 1, "EUR", "25,255"),
            ("USA", "dolar", 1, "USD", "23,140"),
            ("Velká Británie", "libra", 1, "GBP", "29,450"),
            ("Polsko", "zlotý", 1, "PLN", "5,890"),
            ("Švýcarsko", "frank", 1, "CHF", "26,120"),
        });

        // Request date is Saturday (03.05.2025), but CNB returned Friday's response
        var rates = CnbExchangeRateProvider.ParseCnbText(cnbText, new DateOnly(2025, 5, 3));

        // ValidFrom must be the publication date from the header (Friday), not the requested Saturday
        rates.ShouldAllBe(r => r.ValidFrom == new DateOnly(2025, 5, 2));
    }

    [Fact]
    public void ParseCnbText_CommaAsDecimalSeparator_ParsedCorrectly()
    {
        // CNB uses comma as decimal separator (Czech locale)
        var cnbText = BuildSampleCnbResponse("02.05.2025", new[]
        {
            ("EMU", "euro", 1, "EUR", "25,255"),       // single comma
            ("Japonsko", "jen", 100, "JPY", "15,123"), // 3 decimal places
            ("Velká Británie", "libra", 1, "GBP", "29,45"),  // 2 decimal places
            ("Polsko", "zlotý", 1, "PLN", "5,890"),
            ("Švýcarsko", "frank", 1, "CHF", "26,120"),
        });

        var rates = CnbExchangeRateProvider.ParseCnbText(cnbText, null);

        rates.First(r => r.CurrencyCode == "EUR").Rate.ShouldBe(25.255m);
        rates.First(r => r.CurrencyCode == "JPY").Rate.ShouldBe(15.123m);
        rates.First(r => r.CurrencyCode == "GBP").Rate.ShouldBe(29.45m);
    }

    // ─── Error handling ───────────────────────────────────────────────────────

    [Fact]
    public void ParseCnbText_EmptyInput_ThrowsFormatException()
    {
        // Arrange
        var act = () => CnbExchangeRateProvider.ParseCnbText(string.Empty, null);

        // Assert
        act.ShouldThrow<FormatException>()
           .Message.ShouldContain("empty");
    }

    [Fact]
    public void ParseCnbText_TooFewLines_ThrowsFormatException()
    {
        // Only 2 lines — not enough for header + column row + data
        const string cnbText = "02.05.2025 #85\nzeme|mena|mnozstvi|kod|kurz";

        var act = () => CnbExchangeRateProvider.ParseCnbText(cnbText, null);

        act.ShouldThrow<FormatException>();
    }

    [Fact]
    public void ParseCnbText_InvalidDateInHeader_ThrowsFormatException()
    {
        const string cnbText = "INVALID_DATE #85\nzeme|mena|mnozstvi|kod|kurz\nEMU|euro|1|EUR|25,255";

        var act = () => CnbExchangeRateProvider.ParseCnbText(cnbText, null);

        act.ShouldThrow<FormatException>()
           .Message.ShouldContain("could not be parsed");
    }

    [Fact]
    public void ParseCnbText_TooFewValidRates_ThrowsFormatException()
    {
        // Only 2 valid data rows — below the minimum of 5
        var cnbText = BuildSampleCnbResponse("02.05.2025", new[]
        {
            ("EMU", "euro", 1, "EUR", "25,255"),
            ("USA", "dolar", 1, "USD", "23,140"),
        });

        var act = () => CnbExchangeRateProvider.ParseCnbText(cnbText, null);

        act.ShouldThrow<FormatException>()
           .Message.ShouldContain("minimum expected");
    }

    [Fact]
    public void ParseCnbText_MalformedLinesSkipped_ValidLinesStillParsed()
    {
        // Arrange — mix of valid lines and garbage (wrong column count)
        // Make sure we still have >= 5 valid lines
        var cnbText = BuildSampleCnbResponseWithNoise("02.05.2025");

        // Act — should not throw; malformed lines are skipped
        var rates = CnbExchangeRateProvider.ParseCnbText(cnbText, null);

        // Assert — at least 5 valid rates present
        rates.Count.ShouldBeGreaterThanOrEqualTo(5);
        rates.ShouldContain(r => r.CurrencyCode == "EUR");
    }

    // ─── Helper methods ───────────────────────────────────────────────────────

    /// <summary>Builds a minimal valid CNB text response.</summary>
    private static string BuildSampleCnbResponse(
        string headerDate,
        IEnumerable<(string country, string currencyName, int amount, string code, string rate)> currencies)
    {
        var lines = new List<string>
        {
            $"{headerDate} #85",
            "země|měna|množství|kód|kurz"
        };

        foreach (var (country, name, amount, code, rate) in currencies)
        {
            lines.Add($"{country}|{name}|{amount}|{code}|{rate}");
        }

        return string.Join("\n", lines);
    }

    /// <summary>Builds a response that includes some malformed lines (wrong column count).</summary>
    private static string BuildSampleCnbResponseWithNoise(string headerDate)
    {
        return $"""
            {headerDate} #85
            země|měna|množství|kód|kurz
            EMU|euro|1|EUR|25,255
            GARBAGE_LINE_NO_PIPES
            USA|dolar|1|USD|23,140
            ANOTHER|GARBAGE
            Velká Británie|libra|1|GBP|29,450
            Polsko|zlotý|1|PLN|5,890
            Švýcarsko|frank|1|CHF|26,120
            """;
    }
}

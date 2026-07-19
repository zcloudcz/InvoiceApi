using Fakvio.Application.Service;
using Fakvio.Domain.Enums;
using Fakvio.Infrastructure.Service;
using Shouldly;

namespace Fakvio.Tests.Unit;

/// <summary>
/// Unit tests for <see cref="AiBankEmailParser"/> response-parsing and body-preparation
/// logic. The AI call itself is not exercised here — we only test the deterministic
/// JSON → <see cref="BankEmailParsed"/> mapping and the HTML-stripping helper.
///
/// Why not mock IAiProvider? The parsing pure-function layer has enough surface
/// to justify its own tests; integration with IAiProvider is covered in the
/// integration test project with a fake provider returning canned JSON.
/// </summary>
public class AiBankEmailParserTests
{
    // ─── ParseAiResponse ─────────────────────────────────────────────────

    [Fact]
    public void ParseAiResponse_HappyPath_MapsAllFields()
    {
        const string json = """
        {
          "is_payment": true,
          "amount": 1234.56,
          "currency_code": "CZK",
          "direction": "incoming",
          "transaction_date": "2026-04-20T10:15:00Z",
          "variable_symbol": "2026001",
          "constant_symbol": "0308",
          "specific_symbol": null,
          "counterparty_account": "1234567890/0100",
          "counterparty_name": "ACME s.r.o.",
          "message": "faktura 2026001",
          "confidence": 0.95
        }
        """;

        var result = AiBankEmailParser.ParseAiResponse(json, "TestProvider");

        result.ShouldNotBeNull();
        result.Amount.ShouldBe(1234.56m);
        result.CurrencyCode.ShouldBe("CZK");
        result.Direction.ShouldBe(EPaymentDirection.Incoming);
        result.TransactionDate.ShouldBe(new DateTime(2026, 4, 20, 10, 15, 0, DateTimeKind.Utc));
        result.VariableSymbol.ShouldBe("2026001");
        result.ConstantSymbol.ShouldBe("0308");
        result.SpecificSymbol.ShouldBeNull();
        result.CounterpartyAccount.ShouldBe("1234567890/0100");
        result.CounterpartyName.ShouldBe("ACME s.r.o.");
        result.Confidence.ShouldBe(0.95m);
        result.ModelUsed.ShouldBe("TestProvider");
    }

    [Fact]
    public void ParseAiResponse_CardPayment_NoAccountNoSymbols_ParsesWithTransactionCode()
    {
        // Card payment: merchant name only, no account, no symbols — must still
        // parse; "Kód transakce" lands in TransactionCode.
        const string json = """
        {
          "is_payment": true,
          "amount": 2254.95,
          "currency_code": "CZK",
          "direction": "outgoing",
          "transaction_date": "2026-07-09T14:37:00Z",
          "variable_symbol": null,
          "constant_symbol": null,
          "specific_symbol": null,
          "counterparty_account": null,
          "counterparty_name": "ANTHROPIC* CLAUDE SUB, SAN FRANCISCO, CA",
          "message": "Platba kartou",
          "transaction_code": "27427871883",
          "confidence": 0.92
        }
        """;

        var result = AiBankEmailParser.ParseAiResponse(json, "TestProvider");

        result.ShouldNotBeNull();
        result.Direction.ShouldBe(EPaymentDirection.Outgoing);
        result.CounterpartyAccount.ShouldBeNull();
        result.CounterpartyName.ShouldBe("ANTHROPIC* CLAUDE SUB, SAN FRANCISCO, CA");
        result.TransactionCode.ShouldBe("27427871883");
    }

    [Fact]
    public void ParseAiResponse_MissingTransactionCode_MapsNull()
    {
        // Older bank-transfer notifications have no transaction code field.
        const string json = """
        {
          "is_payment": true,
          "amount": 100,
          "currency_code": "CZK",
          "direction": "incoming",
          "transaction_date": "2026-07-09T10:00:00Z",
          "counterparty_account": "123/0100",
          "confidence": 0.9
        }
        """;

        var result = AiBankEmailParser.ParseAiResponse(json, "P");

        result.ShouldNotBeNull();
        result.TransactionCode.ShouldBeNull();
    }

    [Fact]
    public void ParseAiResponse_NotAPayment_ReturnsNull()
    {
        const string json = """{ "is_payment": false }""";

        var result = AiBankEmailParser.ParseAiResponse(json, "P");

        result.ShouldBeNull();
    }

    [Fact]
    public void ParseAiResponse_InvalidJson_ReturnsNull()
    {
        var result = AiBankEmailParser.ParseAiResponse("this is not json", "P");
        result.ShouldBeNull();
    }

    [Fact]
    public void ParseAiResponse_StripsMarkdownCodeFence()
    {
        const string json = """
        ```json
        { "is_payment": true, "amount": 100, "currency_code": "CZK",
          "direction": "incoming", "transaction_date": "2026-01-01T00:00:00Z", "confidence": 0.9 }
        ```
        """;

        var result = AiBankEmailParser.ParseAiResponse(json, "P");

        result.ShouldNotBeNull();
        result.Amount.ShouldBe(100m);
    }

    [Fact]
    public void ParseAiResponse_MissingAmount_ReturnsNull()
    {
        const string json = """
        { "is_payment": true, "currency_code": "CZK",
          "direction": "incoming", "transaction_date": "2026-01-01T00:00:00Z", "confidence": 0.9 }
        """;

        AiBankEmailParser.ParseAiResponse(json, "P").ShouldBeNull();
    }

    [Fact]
    public void ParseAiResponse_ZeroAmount_ReturnsNull()
    {
        // 0 is not a valid payment; guard against accidental matches.
        const string json = """
        { "is_payment": true, "amount": 0, "currency_code": "CZK",
          "direction": "incoming", "transaction_date": "2026-01-01T00:00:00Z", "confidence": 0.9 }
        """;

        AiBankEmailParser.ParseAiResponse(json, "P").ShouldBeNull();
    }

    [Fact]
    public void ParseAiResponse_UnknownDirection_ReturnsNull()
    {
        const string json = """
        { "is_payment": true, "amount": 100, "currency_code": "CZK",
          "direction": "sideways", "transaction_date": "2026-01-01T00:00:00Z", "confidence": 0.9 }
        """;

        AiBankEmailParser.ParseAiResponse(json, "P").ShouldBeNull();
    }

    [Fact]
    public void ParseAiResponse_VariableSymbolWithLetters_CleansToDigits()
    {
        const string json = """
        { "is_payment": true, "amount": 100, "currency_code": "CZK",
          "direction": "incoming", "transaction_date": "2026-01-01T00:00:00Z",
          "variable_symbol": "VS-12 34 56-ABC", "confidence": 0.9 }
        """;

        var result = AiBankEmailParser.ParseAiResponse(json, "P");

        result.ShouldNotBeNull();
        // Non-digit chars are stripped; only first 10 digits kept.
        result.VariableSymbol.ShouldBe("123456");
    }

    [Fact]
    public void ParseAiResponse_VariableSymbolTooLong_TruncatedTo10Digits()
    {
        const string json = """
        { "is_payment": true, "amount": 100, "currency_code": "CZK",
          "direction": "incoming", "transaction_date": "2026-01-01T00:00:00Z",
          "variable_symbol": "12345678901234", "confidence": 0.9 }
        """;

        var result = AiBankEmailParser.ParseAiResponse(json, "P");

        result.ShouldNotBeNull();
        result.VariableSymbol.ShouldBe("1234567890");
    }

    [Fact]
    public void ParseAiResponse_ConfidenceOutOfRange_Clamped()
    {
        const string json = """
        { "is_payment": true, "amount": 100, "currency_code": "CZK",
          "direction": "incoming", "transaction_date": "2026-01-01T00:00:00Z",
          "confidence": 1.5 }
        """;

        var result = AiBankEmailParser.ParseAiResponse(json, "P");

        result.ShouldNotBeNull();
        result.Confidence.ShouldBe(1m);
    }

    [Fact]
    public void ParseAiResponse_CurrencyLowercase_NormalizedToUpper()
    {
        const string json = """
        { "is_payment": true, "amount": 100, "currency_code": "czk",
          "direction": "incoming", "transaction_date": "2026-01-01T00:00:00Z", "confidence": 0.9 }
        """;

        var result = AiBankEmailParser.ParseAiResponse(json, "P");
        result!.CurrencyCode.ShouldBe("CZK");
    }

    // ─── PrepareBody ────────────────────────────────────────────────────

    [Fact]
    public void PrepareBody_PrefersTextBodyOverHtml()
    {
        var input = new BankEmailInput(
            From: "a@b",
            Subject: "s",
            TextBody: "plain-text content",
            HtmlBody: "<p>HTML content</p>",
            ReceivedAt: DateTime.UtcNow);

        var body = AiBankEmailParser.PrepareBody(input);

        body.ShouldBe("plain-text content");
    }

    [Fact]
    public void PrepareBody_FallsBackToRawHtml()
    {
        // By design we DO NOT strip HTML client-side — the AI reads tags directly.
        var input = new BankEmailInput(
            From: "a@b",
            Subject: "s",
            TextBody: null,
            HtmlBody: "<p>only html</p>",
            ReceivedAt: DateTime.UtcNow);

        var body = AiBankEmailParser.PrepareBody(input);

        // Tags must survive — the AI is the parser, not a local regex.
        body.ShouldBe("<p>only html</p>");
    }

    [Fact]
    public void PrepareBody_CollapsesWhitespace()
    {
        var input = new BankEmailInput(
            From: "a@b",
            Subject: "s",
            TextBody: "line1\n\n  line2\t\t line3",
            HtmlBody: null,
            ReceivedAt: DateTime.UtcNow);

        var body = AiBankEmailParser.PrepareBody(input);

        body.ShouldBe("line1 line2 line3");
    }

    [Fact]
    public void PrepareBody_TruncatesVeryLongInput()
    {
        var longText = new string('x', 10_000);
        var input = new BankEmailInput(
            From: "a", Subject: "s", TextBody: longText, HtmlBody: null,
            ReceivedAt: DateTime.UtcNow);

        var body = AiBankEmailParser.PrepareBody(input);

        // Cap is 4000 chars.
        body.Length.ShouldBe(4000);
    }
}

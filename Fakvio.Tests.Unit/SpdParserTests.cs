using Fakvio.Application.QrPayment;
using Shouldly;

namespace Fakvio.Tests.Unit;

/// <summary>
/// Unit tests for <see cref="SpdParser"/> — the reverse of <see cref="SpdIntegrator"/>.
/// Tests cover simple SPD parsing, SPD with embedded SIND (X-INV), and error handling.
///
/// Testing strategy:
/// 1. Simple SPD: BuildSimpleSpdString → Parse → verify payment fields
/// 2. SPD + SIND: BuildSpdWithInvoice → Parse → verify both payment and invoice fields
/// 3. Edge cases: null, empty, wrong prefix, missing attributes
/// </summary>
public class SpdParserTests
{
    // ─── Simple SPD round-trip tests ─────────────────────────────────────

    [Fact]
    public void Parse_SimpleSpd_RoundTrip_MatchesOriginal()
    {
        // Arrange: build a simple SPD string (payment data only, no X-INV)
        var spdString = SpdIntegrator.BuildSimpleSpdString(
            iban: "CZ5855000000001265098001",
            swift: "RZBCCZPP",
            amount: 5850.00m,
            currencyCode: "CZK",
            dueDate: new DateTime(2026, 3, 15),
            variableSymbol: "2026001",
            message: "INV2026001");

        // Act
        var result = SpdParser.Parse(spdString);

        // Assert
        result.ShouldNotBeNull();
        result.IBAN.ShouldBe("CZ5855000000001265098001");
        result.SWIFT.ShouldBe("RZBCCZPP");
        result.Amount.ShouldBe(5850.00m);
        result.Currency.ShouldBe("CZK");
        result.DueDate.ShouldBe(new DateTime(2026, 3, 15));
        result.VariableSymbol.ShouldBe("2026001");
        result.Message.ShouldBe("INV2026001");
        result.EmbeddedSind.ShouldBeNull(); // No X-INV in simple SPD
    }

    [Fact]
    public void Parse_SimpleSpd_NoSwift_IbanOnly()
    {
        // Arrange: SPD without SWIFT code
        var spdString = SpdIntegrator.BuildSimpleSpdString(
            iban: "CZ5855000000001265098001",
            swift: null,
            amount: 1000.00m,
            currencyCode: "CZK",
            dueDate: null,
            variableSymbol: null,
            message: null);

        // Act
        var result = SpdParser.Parse(spdString);

        // Assert
        result.ShouldNotBeNull();
        result.IBAN.ShouldBe("CZ5855000000001265098001");
        result.SWIFT.ShouldBeNull();
        result.Amount.ShouldBe(1000.00m);
        result.DueDate.ShouldBeNull();
        result.VariableSymbol.ShouldBeNull();
    }

    // ─── SPD with embedded SIND (X-INV) round-trip tests ─────────────────

    [Fact]
    public void Parse_SpdWithInvoice_RoundTrip_MergesSharedKeys()
    {
        // Arrange: build SIND, then wrap in SPD with X-INV
        var sindBuilder = new SindBuilder()
            .SetDocumentId("FV2026001")
            .SetIssueDate(new DateTime(2026, 3, 1))
            .SetAmount(5850.00m)
            .SetVariableSymbol("2026001")
            .SetAccount("CZ5855000000001265098001", "RZBCCZPP")
            .SetCurrency("CZK")
            .SetDueDate(new DateTime(2026, 3, 15))
            .SetIssuerRegistrationNumber("12345678")
            .SetIssuerTaxNumber("CZ12345678");

        var spdString = SpdIntegrator.BuildSpdWithInvoice(sindBuilder.GetAttributes());

        // Act
        var result = SpdParser.Parse(spdString);

        // Assert: SPD-level payment data
        result.ShouldNotBeNull();
        result.IBAN.ShouldBe("CZ5855000000001265098001");
        result.SWIFT.ShouldBe("RZBCCZPP");
        result.Amount.ShouldBe(5850.00m);
        result.Currency.ShouldBe("CZK");
        result.DueDate.ShouldBe(new DateTime(2026, 3, 15));
        result.VariableSymbol.ShouldBe("2026001");

        // Assert: embedded SIND should have invoice-specific fields
        result.EmbeddedSind.ShouldNotBeNull();
        result.EmbeddedSind.DocumentNumber.ShouldBe("FV2026001");
        result.EmbeddedSind.IssueDate.ShouldBe(new DateTime(2026, 3, 1));
        result.EmbeddedSind.IssuerRegistrationNumber.ShouldBe("12345678");
        result.EmbeddedSind.IssuerTaxNumber.ShouldBe("CZ12345678");

        // Assert: shared keys should be merged back into SIND
        result.EmbeddedSind.Amount.ShouldBe(5850.00m);
        result.EmbeddedSind.Currency.ShouldBe("CZK");
        result.EmbeddedSind.DueDate.ShouldBe(new DateTime(2026, 3, 15));
        result.EmbeddedSind.IBAN.ShouldBe("CZ5855000000001265098001");
        result.EmbeddedSind.SWIFT.ShouldBe("RZBCCZPP");
        result.EmbeddedSind.VariableSymbol.ShouldBe("2026001");
    }

    // ─── ToExtractedData tests ───────────────────────────────────────────

    [Fact]
    public void ToExtractedData_WithEmbeddedSind_UsesSindData()
    {
        // Arrange: SPD with embedded SIND
        var sindBuilder = new SindBuilder()
            .SetDocumentId("FV2026001")
            .SetIssueDate(new DateTime(2026, 3, 1))
            .SetAmount(5850.00m)
            .SetAccount("CZ5855000000001265098001")
            .SetCurrency("CZK")
            .SetIssuerRegistrationNumber("12345678");

        var spdString = SpdIntegrator.BuildSpdWithInvoice(sindBuilder.GetAttributes());
        var spdData = SpdParser.Parse(spdString)!;

        // Act
        var extracted = spdData.ToExtractedData();

        // Assert: should have invoice-specific fields from SIND
        extracted.DocumentNumber.ShouldBe("FV2026001");
        extracted.IssuerRegistrationNumber.ShouldBe("12345678");
        extracted.Source.ShouldBe(EExtractionSource.QrCode);
    }

    [Fact]
    public void ToExtractedData_WithoutSind_OnlyPaymentFields()
    {
        // Arrange: simple SPD without X-INV
        var spdString = SpdIntegrator.BuildSimpleSpdString(
            iban: "CZ5855000000001265098001",
            swift: null,
            amount: 1234.56m,
            currencyCode: "EUR",
            dueDate: new DateTime(2026, 6, 1),
            variableSymbol: "999",
            message: null);

        var spdData = SpdParser.Parse(spdString)!;

        // Act
        var extracted = spdData.ToExtractedData();

        // Assert: only payment fields populated (no invoice-specific data)
        extracted.TotalAmount.ShouldBe(1234.56m);
        extracted.Currency.ShouldBe("EUR");
        extracted.DueDate.ShouldBe(new DateTime(2026, 6, 1));
        extracted.VariableSymbol.ShouldBe("999");
        extracted.IBAN.ShouldBe("CZ5855000000001265098001");
        extracted.DocumentNumber.ShouldBeNull(); // Not available in pure SPD
        extracted.IssuerRegistrationNumber.ShouldBeNull();
    }

    // ─── Error handling tests ────────────────────────────────────────────

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Parse_NullOrEmpty_ReturnsNull(string? input)
    {
        SpdParser.Parse(input).ShouldBeNull();
    }

    [Fact]
    public void Parse_WrongPrefix_ReturnsNull()
    {
        // SIND prefix instead of SPD
        SpdParser.Parse("SID*1.0*AM:1000.00*").ShouldBeNull();
    }

    [Fact]
    public void Parse_NoAttributes_ReturnsNull()
    {
        SpdParser.Parse("SPD*1.0*").ShouldBeNull();
    }

    // ─── Detection tests ─────────────────────────────────────────────────

    [Theory]
    [InlineData("SPD*1.0*AM:100*", true)]
    [InlineData("SID*1.0*ID:FV001*", false)]
    [InlineData("random text", false)]
    [InlineData(null, false)]
    public void IsSpdString_DetectsCorrectly(string? input, bool expected)
    {
        SpdParser.IsSpdString(input).ShouldBe(expected);
    }

    // ─── Manual SPD string parsing (not from builder) ────────────────────

    [Fact]
    public void Parse_ManualSpdString_ParsesCorrectly()
    {
        // Arrange: manually constructed SPD string (as would appear in a real QR code)
        var spdString = "SPD*1.0*ACC:CZ5855000000001265098001+RZBCCZPP*AM:999.50*CC:EUR*DT:20261231*MSG:Payment ref*X-VS:12345";

        // Act
        var result = SpdParser.Parse(spdString);

        // Assert
        result.ShouldNotBeNull();
        result.IBAN.ShouldBe("CZ5855000000001265098001");
        result.SWIFT.ShouldBe("RZBCCZPP");
        result.Amount.ShouldBe(999.50m);
        result.Currency.ShouldBe("EUR");
        result.DueDate.ShouldBe(new DateTime(2026, 12, 31));
        result.Message.ShouldBe("Payment ref");
        result.VariableSymbol.ShouldBe("12345");
    }
}

using Fakvio.Application.QrPayment;
using Shouldly;

namespace Fakvio.Tests.Unit;

/// <summary>
/// Unit tests for <see cref="SindParser"/> — the reverse of <see cref="SindBuilder"/>.
/// Tests cover round-trip parsing, CRC validation, edge cases, and error handling.
///
/// The key testing strategy is "round-trip": build a SIND string with SindBuilder,
/// then parse it back with SindParser and verify all fields match.
/// This ensures the parser is exactly the inverse of the builder.
/// </summary>
public class SindParserTests
{
    // ─── Round-trip tests ────────────────────────────────────────────────

    [Fact]
    public void Parse_RoundTrip_RequiredFieldsOnly_MatchesOriginal()
    {
        // Arrange: build a SIND string with only the 3 required fields
        var sindString = new SindBuilder()
            .SetDocumentId("FV2026001")
            .SetIssueDate(new DateTime(2026, 3, 1))
            .SetAmount(5850.00m)
            .Build();

        // Act: parse it back
        var result = SindParser.Parse(sindString);

        // Assert: all required fields should match
        result.ShouldNotBeNull();
        result.DocumentNumber.ShouldBe("FV2026001");
        result.IssueDate.ShouldBe(new DateTime(2026, 3, 1));
        result.Amount.ShouldBe(5850.00m);
    }

    [Fact]
    public void Parse_RoundTrip_AllFields_MatchesOriginal()
    {
        // Arrange: build a SIND string with all possible fields
        var sindString = new SindBuilder()
            .SetDocumentId("FV2026042")
            .SetIssueDate(new DateTime(2026, 3, 10))
            .SetAmount(12100.00m)
            .SetVariableSymbol("2026042")
            .SetAccount("CZ5855000000001265098001", "RZBCCZPP")
            .SetCurrency("CZK")
            .SetDueDate(new DateTime(2026, 3, 24))
            .SetIssuerTaxNumber("CZ12345678")
            .SetIssuerRegistrationNumber("12345678")
            .SetRecipientTaxNumber("CZ87654321")
            .SetRecipientRegistrationNumber("87654321")
            .SetTaxableSupplyDate(new DateTime(2026, 3, 10))
            .SetStandardVat(10000.00m, 2100.00m)
            .SetReducedVat1(500.00m, 75.00m)
            .SetReducedVat2(200.00m, 20.00m)
            .SetNonTaxableAmount(300.00m)
            .SetDocumentType(9)
            .SetSoftware("Fakvio")
            .Build();

        // Act
        var result = SindParser.Parse(sindString);

        // Assert: every field should be correctly parsed
        result.ShouldNotBeNull();
        result.DocumentNumber.ShouldBe("FV2026042");
        result.IssueDate.ShouldBe(new DateTime(2026, 3, 10));
        result.Amount.ShouldBe(12100.00m);
        result.VariableSymbol.ShouldBe("2026042");
        result.IBAN.ShouldBe("CZ5855000000001265098001");
        result.SWIFT.ShouldBe("RZBCCZPP");
        result.Currency.ShouldBe("CZK");
        result.DueDate.ShouldBe(new DateTime(2026, 3, 24));
        result.IssuerTaxNumber.ShouldBe("CZ12345678");
        result.IssuerRegistrationNumber.ShouldBe("12345678");
        result.RecipientTaxNumber.ShouldBe("CZ87654321");
        result.RecipientRegistrationNumber.ShouldBe("87654321");
        result.TaxableSupplyDate.ShouldBe(new DateTime(2026, 3, 10));
        result.StandardVatBase.ShouldBe(10000.00m);
        result.StandardVatAmount.ShouldBe(2100.00m);
        result.ReducedVat1Base.ShouldBe(500.00m);
        result.ReducedVat1Amount.ShouldBe(75.00m);
        result.ReducedVat2Base.ShouldBe(200.00m);
        result.ReducedVat2Amount.ShouldBe(20.00m);
        result.NonTaxableAmount.ShouldBe(300.00m);
        result.DocumentType.ShouldBe(9);
        result.Software.ShouldBe("Fakvio");
    }

    [Fact]
    public void Parse_RoundTrip_AccountWithoutSwift_IbanOnly()
    {
        // Arrange: ACC without +BIC part
        var sindString = new SindBuilder()
            .SetDocumentId("FV001")
            .SetIssueDate(new DateTime(2026, 1, 1))
            .SetAmount(1000.00m)
            .SetAccount("CZ5855000000001265098001") // No SWIFT
            .Build();

        // Act
        var result = SindParser.Parse(sindString);

        // Assert
        result.ShouldNotBeNull();
        result.IBAN.ShouldBe("CZ5855000000001265098001");
        result.SWIFT.ShouldBeNull();
    }

    // ─── CRC validation tests ────────────────────────────────────────────

    [Fact]
    public void Parse_InvalidCrc_ReturnsNull()
    {
        // Arrange: valid SIND structure but tampered CRC
        var sindString = "SID*1.0*AM:1000.00*DD:20260301*ID:FV001*CRC32:00000000";

        // Act
        var result = SindParser.Parse(sindString, validateCrc: true);

        // Assert: invalid CRC should cause parse failure
        result.ShouldBeNull();
    }

    [Fact]
    public void Parse_MissingCrc_WithValidation_ReturnsNull()
    {
        // Arrange: valid SIND structure but no CRC32 token
        var sindString = "SID*1.0*AM:1000.00*DD:20260301*ID:FV001*";

        // Act
        var result = SindParser.Parse(sindString, validateCrc: true);

        // Assert: missing CRC should fail when validation is enabled
        result.ShouldBeNull();
    }

    [Fact]
    public void Parse_MissingCrc_WithoutValidation_Succeeds()
    {
        // Arrange: valid SIND structure but no CRC32 token
        var sindString = "SID*1.0*AM:1000.00*DD:20260301*ID:FV001*";

        // Act: disable CRC validation (useful for reduced SIND in SPD)
        var result = SindParser.Parse(sindString, validateCrc: false);

        // Assert: should parse successfully
        result.ShouldNotBeNull();
        result.DocumentNumber.ShouldBe("FV001");
        result.Amount.ShouldBe(1000.00m);
        result.IssueDate.ShouldBe(new DateTime(2026, 3, 1));
    }

    // ─── Error handling tests ────────────────────────────────────────────

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Parse_NullOrEmpty_ReturnsNull(string? input)
    {
        SindParser.Parse(input).ShouldBeNull();
    }

    [Fact]
    public void Parse_WrongPrefix_ReturnsNull()
    {
        // SPD prefix instead of SID
        SindParser.Parse("SPD*1.0*AM:1000.00*").ShouldBeNull();
    }

    [Fact]
    public void Parse_NoAttributes_ReturnsNull()
    {
        // Just the prefix, no attributes
        SindParser.Parse("SID*1.0*").ShouldBeNull();
    }

    // ─── Detection tests ─────────────────────────────────────────────────

    [Theory]
    [InlineData("SID*1.0*ID:FV001*", true)]
    [InlineData("SPD*1.0*AM:100*", false)]
    [InlineData("random text", false)]
    [InlineData(null, false)]
    [InlineData("", false)]
    public void IsSindString_DetectsCorrectly(string? input, bool expected)
    {
        SindParser.IsSindString(input).ShouldBe(expected);
    }

    // ─── ToExtractedData conversion tests ────────────────────────────────

    [Fact]
    public void ToExtractedData_ComputesTotalVatAndBase()
    {
        // Arrange: build SIND with VAT breakdown, parse it
        var sindString = new SindBuilder()
            .SetDocumentId("FV001")
            .SetIssueDate(new DateTime(2026, 1, 1))
            .SetAmount(12395.00m)
            .SetStandardVat(10000.00m, 2100.00m)
            .SetReducedVat1(200.00m, 30.00m)
            .SetNonTaxableAmount(65.00m)
            .Build();

        var sindData = SindParser.Parse(sindString);

        // Act
        var extracted = sindData!.ToExtractedData();

        // Assert: TotalBeforeVat = 10000 + 200 + 65 = 10265
        extracted.TotalBeforeVat.ShouldBe(10265.00m);
        // TotalVat = 2100 + 30 = 2130
        extracted.TotalVat.ShouldBe(2130.00m);
        extracted.Source.ShouldBe(EExtractionSource.QrCode);
    }
}

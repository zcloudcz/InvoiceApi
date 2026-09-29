using Fakvio.Domain.Entities;
using Fakvio.Domain.Enums;
using Fakvio.Infrastructure.Service.Ubl;
using Shouldly;
using Xunit;

namespace Fakvio.Tests.Unit;

/// <summary>
/// Theory tests for the lookup tables in <c>UblCodes</c> (ADR 0002, F1.1).
/// Every row here mirrors a row in the ADR §4.1.2 tables — a failing test means either the
/// code or the ADR is wrong, not that the test is out of date.
/// </summary>
public class UblCodesTests
{
    // --------------------------------------------------------------------------
    // CountryToIso2
    // --------------------------------------------------------------------------

    [Theory]
    [InlineData(null, "CZ")]
    [InlineData("", "CZ")]
    [InlineData("  ", "CZ")]
    [InlineData("cz", "CZ")]
    [InlineData("SK", "SK")]
    [InlineData("gb", "GB")]
    [InlineData("Česká republika", "CZ")]
    [InlineData("Česko", "CZ")]
    [InlineData("Czech Republic", "CZ")]
    [InlineData("Slovensko", "SK")]
    [InlineData("Slovenská republika", "SK")]
    [InlineData("Slovakia", "SK")]
    [InlineData("Německo", "DE")]
    [InlineData("Deutschland", "DE")]
    [InlineData("Rakousko", "AT")]
    [InlineData("Österreich", "AT")]
    [InlineData("Polsko", "PL")]
    [InlineData("Polska", "PL")]
    public void CountryToIso2_KnownValues_MapCorrectly(string? input, string expected)
        => UblCodes.CountryToIso2(input).ShouldBe(expected);

    [Fact]
    public void CountryToIso2_UnknownFreeText_ReturnsNull()
        => UblCodes.CountryToIso2("Narnia").ShouldBeNull();

    // --------------------------------------------------------------------------
    // UnitToRec20
    // --------------------------------------------------------------------------

    [Theory]
    [InlineData("ks", "H87")]
    [InlineData("KS", "H87")]
    [InlineData("kus", "H87")]
    [InlineData("pcs", "H87")]
    [InlineData("pc", "H87")]
    [InlineData("hod", "HUR")]
    [InlineData("h", "HUR")]
    [InlineData("hr", "HUR")]
    [InlineData("min", "MIN")]
    [InlineData("den", "DAY")]
    [InlineData("d", "DAY")]
    [InlineData("day", "DAY")]
    [InlineData("měs", "MON")]
    [InlineData("mes", "MON")]
    [InlineData("month", "MON")]
    [InlineData("rok", "ANN")]
    [InlineData("year", "ANN")]
    [InlineData("km", "KMT")]
    [InlineData("m", "MTR")]
    [InlineData("m2", "MTK")]
    [InlineData("m²", "MTK")]
    [InlineData("m3", "MTQ")]
    [InlineData("m³", "MTQ")]
    [InlineData("kg", "KGM")]
    [InlineData("g", "GRM")]
    [InlineData("t", "TNE")]
    [InlineData("l", "LTR")]
    [InlineData("bal", "XPK")]
    [InlineData("kpl", "SET")]
    [InlineData("sada", "SET")]
    [InlineData("set", "SET")]
    [InlineData("ks.", "H87")] // trailing dot stripped
    [InlineData(null, "C62")]
    [InlineData("", "C62")]
    [InlineData("banana", "C62")] // unknown -> catch-all, never blocks export
    public void UnitToRec20_MapsKnownUnits(string? input, string expected)
        => UblCodes.UnitToRec20(input).ShouldBe(expected);

    [Fact]
    public void UnitToRec20_AlreadyValidCode_PassesThrough()
        => UblCodes.UnitToRec20("XPP").ShouldBe("XPP");

    // --------------------------------------------------------------------------
    // VatCategory
    // --------------------------------------------------------------------------

    [Fact]
    public void VatCategory_NonVatPayer_AlwaysReturnsO_RegardlessOfRegime()
    {
        var result = UblCodes.VatCategory(EVatRegime.Standard, 21m, issuerIsVatPayer: false);

        result.Code.ShouldBe("O");
        result.Percent.ShouldBeNull();
        result.ExemptionCode.ShouldBe("VATEX-EU-O");
        result.ExemptionTextKey.ShouldBe(UblCodes.ExemptionTextKeys.NotVatPayer);
    }

    [Fact]
    public void VatCategory_Standard_PositiveRate_ReturnsS()
    {
        var result = UblCodes.VatCategory(EVatRegime.Standard, 21m, issuerIsVatPayer: true);

        result.Code.ShouldBe("S");
        result.Percent.ShouldBe(21m);
        result.ExemptionCode.ShouldBeNull();
    }

    [Fact]
    public void VatCategory_Standard_ZeroRate_ReturnsE()
    {
        var result = UblCodes.VatCategory(EVatRegime.Standard, 0m, issuerIsVatPayer: true);

        result.Code.ShouldBe("E");
        result.Percent.ShouldBe(0m);
        result.ExemptionTextKey.ShouldBe(UblCodes.ExemptionTextKeys.Exempt);
    }

    [Fact]
    public void VatCategory_Exempt_ReturnsE()
    {
        var result = UblCodes.VatCategory(EVatRegime.Exempt, 0m, issuerIsVatPayer: true);

        result.Code.ShouldBe("E");
        result.Percent.ShouldBe(0m);
    }

    [Fact]
    public void VatCategory_ReverseCharge_ReturnsAeWithZeroPercent()
    {
        var result = UblCodes.VatCategory(EVatRegime.ReverseCharge, 0m, issuerIsVatPayer: true);

        result.Code.ShouldBe("AE");
        result.Percent.ShouldBe(0m);
        result.ExemptionCode.ShouldBe("VATEX-EU-AE");
        result.ExemptionTextKey.ShouldBe(UblCodes.ExemptionTextKeys.ReverseCharge);
    }

    [Fact]
    public void VatCategory_OutOfScope_VatPayer_ReturnsO()
    {
        var result = UblCodes.VatCategory(EVatRegime.OutOfScope, 0m, issuerIsVatPayer: true);

        result.Code.ShouldBe("O");
        result.ExemptionCode.ShouldBe("VATEX-EU-O");
    }

    // --------------------------------------------------------------------------
    // ExemptionText
    // --------------------------------------------------------------------------

    [Theory]
    [InlineData(null, "cs", null)]
    [InlineData("NotVatPayer", "cs", "Nepodléhá DPH")]
    [InlineData("NotVatPayer", "sk", "Nepodléhá DPH")]
    [InlineData("NotVatPayer", "en", "Not subject to VAT")]
    [InlineData("Exempt", "cs", "Osvobozeno od DPH")]
    [InlineData("Exempt", "en", "Exempt from VAT")]
    [InlineData("ReverseCharge", "cs", "Přenesení daňové povinnosti")]
    [InlineData("ReverseCharge", "en", "Reverse charge")]
    public void ExemptionText_LocalizesByLanguage(string? key, string language, string? expected)
        => UblCodes.ExemptionText(key, language).ShouldBe(expected);

    // --------------------------------------------------------------------------
    // PaymentMeansCode
    // --------------------------------------------------------------------------

    [Theory]
    [InlineData(EPaymentMethod.BankTransfer, true, true, "58")]   // IBAN + EUR -> SEPA
    [InlineData(EPaymentMethod.BankTransfer, true, false, "30")]  // IBAN but not EUR
    [InlineData(EPaymentMethod.BankTransfer, false, true, "30")]  // no IBAN
    [InlineData(EPaymentMethod.Cash, false, false, "10")]
    [InlineData(EPaymentMethod.CreditCard, false, false, "48")]
    [InlineData(EPaymentMethod.PayPal, false, false, "ZZZ")]
    [InlineData(EPaymentMethod.Other, false, false, "ZZZ")]
    [InlineData(null, false, false, "ZZZ")]
    public void PaymentMeansCode_MapsCorrectly(EPaymentMethod? method, bool hasIban, bool currencyIsEur, string expected)
        => UblCodes.PaymentMeansCode(method, hasIban, currencyIsEur).ShouldBe(expected);

    // --------------------------------------------------------------------------
    // EndpointId
    // --------------------------------------------------------------------------

    [Fact]
    public void EndpointId_SkClient_WithSkPrefix_ReturnsScheme0245()
    {
        var client = ClientWith(country: "SK", taxNumber: "SK2020123456");

        var result = UblCodes.EndpointId(client);

        result.ShouldNotBeNull();
        result!.Value.SchemeId.ShouldBe("0245");
        result.Value.Value.ShouldBe("2020123456");
    }

    [Fact]
    public void EndpointId_SkClient_NonVatPayer_BareDigits_ReturnsScheme0245()
    {
        // SK non-VAT-payer only has a 10-digit DIČ, no "SK" prefix and no IČ DPH.
        var client = ClientWith(country: "SK", taxNumber: "2020123456");

        var result = UblCodes.EndpointId(client);

        result.ShouldNotBeNull();
        result!.Value.SchemeId.ShouldBe("0245");
        result.Value.Value.ShouldBe("2020123456");
    }

    [Fact]
    public void EndpointId_CzClient_ReturnsScheme9929()
    {
        var client = ClientWith(country: "CZ", taxNumber: "CZ12345678");

        var result = UblCodes.EndpointId(client);

        result.ShouldNotBeNull();
        result!.Value.SchemeId.ShouldBe("9929");
        result.Value.Value.ShouldBe("CZ12345678");
    }

    [Fact]
    public void EndpointId_UnknownCountry_ReturnsNull()
    {
        var client = ClientWith(country: "Narnia", taxNumber: "12345678");

        UblCodes.EndpointId(client).ShouldBeNull();
    }

    [Fact]
    public void EndpointId_NoTaxNumber_ReturnsNull()
    {
        var client = ClientWith(country: "CZ", taxNumber: null);

        UblCodes.EndpointId(client).ShouldBeNull();
    }

    [Fact]
    public void EndpointId_NullClient_ReturnsNull()
        => UblCodes.EndpointId(null).ShouldBeNull();

    private static Client ClientWith(string country, string? taxNumber) => new()
    {
        TaxNumber = taxNumber,
        Address = new List<Address>
        {
            new() { Country = country, IsPrimary = true }
        }
    };
}

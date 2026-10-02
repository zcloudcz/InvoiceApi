using Fakvio.Application.Common.Helpers;
using Shouldly;

namespace Fakvio.Tests.Unit;

/// <summary>Tests for <see cref="BillingPeriodShifter"/>: periods move, everything else stays untouched.</summary>
public class BillingPeriodShifterTests
{
    [Theory]
    [InlineData("Hosting 3/2026", 1, "Hosting 4/2026")]
    [InlineData("Hosting 03/2026", 1, "Hosting 04/2026")]
    [InlineData("Hosting 12/2026", 1, "Hosting 1/2027")]
    [InlineData("Hosting 1/2026", -1, "Hosting 12/2025")]
    [InlineData("Hosting 3/2026", 12, "Hosting 3/2027")]
    [InlineData("Služby 2026-03", 1, "Služby 2026-04")]
    [InlineData("Služby 2026-12", 2, "Služby 2027-02")]
    [InlineData("Q1/2026", 3, "Q2/2026")]
    [InlineData("Q4 2026", 3, "Q1 2027")]
    [InlineData("1Q 2026", 3, "2Q 2026")]
    [InlineData("4Q/2026", 6, "2Q/2027")]
    [InlineData("Q1/2026", 1, "Q1/2026")]
    [InlineData("za březen 2026", 1, "za duben 2026")]
    [InlineData("Březen 2026", 1, "Duben 2026")]
    [InlineData("za měsíc březen", 1, "za měsíc duben")]
    [InlineData("fakturace za března", 1, "fakturace za dubna")]
    [InlineData("za prosinec 2026", 1, "za leden 2027")]
    [InlineData("za leden 2026", -1, "za prosinec 2025")]
    [InlineData("za červenec", 1, "za srpen")]
    [InlineData("za červen", 1, "za červenec")]
    [InlineData("Hosting March 2026", 1, "Hosting April 2026")]
    [InlineData("December 2026", 1, "January 2027")]
    [InlineData("Hosting 3/2026 a 2026-03 (březen 2026)", 1, "Hosting 4/2026 a 2026-04 (duben 2026)")]
    public void Shift_MovesPeriods(string input, int months, string expected) =>
        BillingPeriodShifter.Shift(input, months).ShouldBe(expected);

    [Theory]
    [InlineData("Faktura 2026001")]
    [InlineData("VS 2026003, IBAN CZ6508000000192000145399")]
    [InlineData("3/4 dne")]
    [InlineData("poměr 3/2")]
    [InlineData("sleva 1,5/2026")]
    [InlineData("splatnost 15/3/2026")]
    [InlineData("datum 2026-03-15")]
    [InlineData("ze dne 15. března 2026")]
    [InlineData("on 15 March 2026")]
    [InlineData("March 15, 2026")]
    [InlineData("You may call us")]
    [InlineData("rok 2026")]
    [InlineData("13/2026")]
    [InlineData("")]
    public void Shift_LeavesNonPeriodsAlone(string input) =>
        BillingPeriodShifter.Shift(input, 1).ShouldBe(input);

    [Fact]
    public void Shift_NullAndZeroMonths_AreNoOps()
    {
        BillingPeriodShifter.Shift(null, 1).ShouldBeNull();
        BillingPeriodShifter.Shift("Hosting 3/2026", 0).ShouldBe("Hosting 3/2026");
    }
}

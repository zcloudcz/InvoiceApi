// ============================================================================
// CzechBankAccountValidatorTests — coverage for issue #154.
//
// Test vectors:
// - IBAN "CZ1408002221213222223238" and account "222121-3222223238/0800" are the SAME
//   BBAN (bank 0800, prefix 222121, account 3222223238), so a valid domestic account and its
//   IBAN form are both exercised from one real number.
// - The account/prefix weighted-sum example (222121-3222223238/0800, S1=55, S2=132) is the
//   published worked example at https://overucet.cz/modulo-11-kontrola-cisla-uctu.php.
// - The IBAN check digits (14) were computed by hand via ISO 7064 MOD 97-10 from that same BBAN
//   and independently verified to reduce to remainder 1 — see the fix's PR description.
// ============================================================================

using Fakvio.Domain.Validation;
using Shouldly;

namespace Fakvio.Tests.Unit;

public class CzechBankAccountValidatorTests
{
    // ─── IsValidIban ────────────────────────────────────────────────────────

    [Theory]
    [InlineData("CZ1408002221213222223238")] // canonical
    [InlineData("CZ14 0800 2221 2132 2222 3238")] // spaces, as banking apps display it
    [InlineData("cz1408002221213222223238")] // lowercase — must be normalized
    public void IsValidIban_ValidChecksum_ReturnsTrue(string iban)
        => CzechBankAccountValidator.IsValidIban(iban).ShouldBeTrue();

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("CZ0000000000000000000000")] // structurally fine, wrong check digits
    [InlineData("CZ1508002221213222223238")] // one check digit off from the valid vector
    [InlineData("CZ1408002221213222223239")] // one BBAN digit off — check digits no longer match
    [InlineData("1408002221213222223238")] // missing country letters
    [InlineData("CZXX08002221213222223238")] // check digits are not digits
    [InlineData("CZ14")] // too short to contain a BBAN at all
    public void IsValidIban_InvalidOrMissing_ReturnsFalse(string? iban)
        => CzechBankAccountValidator.IsValidIban(iban).ShouldBeFalse();

    // ─── IsValidCzechAccountNumber ──────────────────────────────────────────

    [Theory]
    [InlineData("222121-3222223238/0800")] // published worked example, prefix + account
    [InlineData("3222223238/0800")] // same account, no prefix (prefix = 000000 → trivially valid)
    [InlineData("1342333010/3030")] // second real fixture used by QrPaymentTests
    public void IsValidCzechAccountNumber_ValidChecksum_ReturnsTrue(string accountNumber)
        => CzechBankAccountValidator.IsValidCzechAccountNumber(accountNumber).ShouldBeTrue();

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("3222223238")] // no bank code at all
    [InlineData("3222223239/0800")] // one digit off from a valid account — fails modulo 11
    [InlineData("222122-3222223238/0800")] // valid account, but prefix changed — fails modulo 11
    [InlineData("3222223238/080")] // bank code only 3 digits
    [InlineData("3222223238/08000")] // bank code 5 digits
    [InlineData("0000000000/0800")] // all-zero account — passes the checksum but is not a real account
    [InlineData("12A4567890/0800")] // non-digit in the account part
    public void IsValidCzechAccountNumber_InvalidOrMissing_ReturnsFalse(string? accountNumber)
        => CzechBankAccountValidator.IsValidCzechAccountNumber(accountNumber).ShouldBeFalse();

    // ─── HasUsableBankConnection ────────────────────────────────────────────

    [Fact]
    public void HasUsableBankConnection_ValidIbanOnly_ReturnsTrue()
        => CzechBankAccountValidator.HasUsableBankConnection("CZ1408002221213222223238", null)
            .ShouldBeTrue();

    [Fact]
    public void HasUsableBankConnection_ValidAccountOnly_ReturnsTrue()
        => CzechBankAccountValidator.HasUsableBankConnection(null, "222121-3222223238/0800")
            .ShouldBeTrue();

    [Fact]
    public void HasUsableBankConnection_BothInvalidOrMissing_ReturnsFalse()
        => CzechBankAccountValidator.HasUsableBankConnection(null, null).ShouldBeFalse();

    [Fact]
    public void HasUsableBankConnection_BothPresentButInvalid_ReturnsFalse()
        => CzechBankAccountValidator.HasUsableBankConnection("CZ0000000000000000000000", "3222223239/0800")
            .ShouldBeFalse();
}

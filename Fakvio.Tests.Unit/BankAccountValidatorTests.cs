using Fakvio.Domain.Validation;
using Shouldly;

namespace Fakvio.Tests.Unit;

/// <summary>
/// Unit tests for <see cref="BankAccountValidator"/> — the shared rules that decide whether
/// a bank connection can be paid to (issue #154).
///
/// These checksums are the whole point of the fix: before it, a mistyped account number was
/// stored without complaint and only surfaced as an unscannable QR code on an invoice the
/// customer had already received. Every rule below is therefore tested with a value that
/// really exists in the wild (valid) and with the same value carrying a single typo (invalid),
/// because "rejects obvious garbage" is easy and "rejects a plausible typo" is what matters.
/// </summary>
public class BankAccountValidatorTests
{
    // A well-formed Czech IBAN: CZ65 0800 0000 1920 0014 5399.
    // Bank 0800, prefix 000019, account base 2000145399 — all three checksums hold.
    private const string ValidCzechIban = "CZ6508000000192000145399";

    // ─── IBAN ────────────────────────────────────────────────────────────────

    [Theory]
    [InlineData(ValidCzechIban)]
    [InlineData("CZ65 0800 0000 1920 0014 5399")]      // as printed by banks, in groups of four
    [InlineData("cz65-0800-0000-1920-0014-5399")]      // as pasted by users, lower case and dashed
    [InlineData("DE89370400440532013000")]             // foreign IBAN — only mod-97 applies
    [InlineData("SK3112000000198742637541")]
    public void TryValidateIban_WellFormedIban_Accepts(string iban)
    {
        BankAccountValidator.TryValidateIban(iban, out var error).ShouldBeTrue(error);
        error.ShouldBeNull();
    }

    [Fact]
    public void TryValidateIban_CheckDigitTypo_Rejects()
    {
        // Last digit changed 9 → 8. Structurally perfect, mod-97 fails — exactly the kind of
        // typo that used to travel all the way onto a printed QR code.
        BankAccountValidator.TryValidateIban("CZ6508000000192000145398", out var error).ShouldBeFalse();
        error.ShouldNotBeNull();
    }

    [Fact]
    public void TryValidateIban_CzechIbanFailingDomesticMod11_Rejects()
    {
        // CZ07 0800 0000 0012 3456 7890 satisfies mod-97 — the check digits were computed for
        // it — but its embedded domestic account base (1234567890) fails the Czech modulo 11
        // check. A CZ IBAN is only a wrapper around a domestic account number, so mod-97 alone
        // is not enough to call it payable.
        BankAccountValidator.TryValidateIban("CZ0708000000001234567890", out var error).ShouldBeFalse();
        error.ShouldNotBeNull();
        error.ShouldContain("modulo 11");
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("CZ65")]                                // far too short
    [InlineData("1508000000192000145399")]              // digits where the country code belongs
    [InlineData("CZAB08000000192000145399")]            // letters where the check digits belong
    public void TryValidateIban_MalformedInput_Rejects(string? iban)
    {
        BankAccountValidator.TryValidateIban(iban, out var error).ShouldBeFalse();
        error.ShouldNotBeNull();
    }

    [Fact]
    public void NormalizeIban_StripsSeparatorsAndUpperCases()
    {
        // This is the form that ends up in the SPD "ACC" attribute, so it must be free of
        // anything a user might paste along with the number.
        BankAccountValidator.NormalizeIban("cz65 0800-0000 1920 0014 5399")
            .ShouldBe(ValidCzechIban);
    }

    // ─── Czech domestic account number ───────────────────────────────────────

    [Theory]
    [InlineData("19-2000145399/0800")]                  // with prefix
    [InlineData("2000145399/0800")]                     // without prefix
    [InlineData("1342333010/3030")]
    [InlineData("000019-2000145399/0800")]              // zero-padded prefix
    [InlineData("19-2000145399 / 0800")]                // spaces around the separator
    public void TryValidateCzechAccountNumber_WellFormedAccount_Accepts(string accountNumber)
    {
        BankAccountValidator.TryValidateCzechAccountNumber(accountNumber, out var error)
            .ShouldBeTrue(error);
        error.ShouldBeNull();
    }

    [Fact]
    public void TryValidateCzechAccountNumber_FailingMod11_Rejects()
    {
        // 1234567890 is the canonical "looks like an account number" value and is precisely
        // what modulo 11 exists to catch.
        BankAccountValidator.TryValidateCzechAccountNumber("1234567890/0100", out var error)
            .ShouldBeFalse();
        error.ShouldNotBeNull();
        error.ShouldContain("modulo 11");
    }

    [Fact]
    public void TryValidateCzechAccountNumber_ZeroAccount_Rejects()
    {
        // All zeros passes the checksum arithmetic but is not an account anyone can pay to.
        BankAccountValidator.TryValidateCzechAccountNumber("0000000000/0800", out var error)
            .ShouldBeFalse();
        error.ShouldNotBeNull();
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("2000145399")]                          // no bank code
    [InlineData("2000145399/08")]                       // bank code is always four digits
    [InlineData("2000145399/ABCD")]                     // bank code is always numeric
    [InlineData("19-20-2000145399/0800")]               // two dashes is not a Czech format
    [InlineData("20001453991/0800")]                    // 11 digits — longer than the format allows
    [InlineData("ABCDEF/0800")]
    public void TryValidateCzechAccountNumber_NotCzechFormat_Rejects(string? accountNumber)
    {
        BankAccountValidator.TryValidateCzechAccountNumber(accountNumber, out var error)
            .ShouldBeFalse();
        error.ShouldNotBeNull();
    }

    [Theory]
    [InlineData("19-2000145399/0800", true)]
    [InlineData("1234567890/0100", true)]               // Czech-shaped even though the checksum fails
    [InlineData("DE-ACCOUNT-4711", false)]
    [InlineData("2000145399", false)]
    [InlineData(null, false)]
    public void HasCzechAccountNumberFormat_RecognisesShapeNotCorrectness(
        string? accountNumber, bool expected)
    {
        // Shape and correctness are deliberately separate questions: shape decides WHICH rules
        // apply, so a Czech-shaped number with a typo must still be recognised as Czech —
        // otherwise it would slip through as an unverifiable "foreign" account.
        BankAccountValidator.HasCzechAccountNumberFormat(accountNumber).ShouldBe(expected);
    }

    // ─── Stored account number (shape-driven rule) ───────────────────────────

    [Fact]
    public void TryValidateAccountNumber_ForeignFreeFormAccount_Accepts()
    {
        // The entity explicitly allows non-Czech account numbers and there is no universal
        // checksum for them. They are storable — they just cannot produce a QR payment code,
        // which QrPaymentService is the one to refuse.
        BankAccountValidator.TryValidateAccountNumber("DE-ACCOUNT-4711", out var error)
            .ShouldBeTrue(error);
        error.ShouldBeNull();
    }

    [Fact]
    public void TryValidateAccountNumber_CzechShapedWithTypo_Rejects()
    {
        BankAccountValidator.TryValidateAccountNumber("1234567890/0100", out var error)
            .ShouldBeFalse();
        error.ShouldNotBeNull();
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void TryValidateAccountNumber_Empty_Rejects(string? accountNumber)
    {
        // AccountNumber is required on the entity, so "empty" is a rejection, not a pass-through.
        BankAccountValidator.TryValidateAccountNumber(accountNumber, out var error).ShouldBeFalse();
        error.ShouldNotBeNull();
    }

    // ─── The examples the UI shows must survive the UI's own validation ──────

    [Fact]
    public void ExamplesShownInBankAccountDialog_AreValid()
    {
        // BankAccountDialog now validates as the user types. A placeholder or helper text that
        // its own field would reject teaches the user the wrong format — this test pins the
        // two examples to the rules. Keep in sync with BankAccountDialog.razor and the
        // BankAccount_AccountNumberHelper resource string.
        BankAccountValidator.TryValidateAccountNumber("19-2000145399/0800", out var accountError)
            .ShouldBeTrue(accountError);
        BankAccountValidator.TryValidateIban("CZ65 0800 0000 1920 0014 5399", out var ibanError)
            .ShouldBeTrue(ibanError);
    }
}

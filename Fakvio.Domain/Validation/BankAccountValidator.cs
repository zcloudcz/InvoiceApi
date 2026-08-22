using System.Diagnostics.CodeAnalysis;

namespace Fakvio.Domain.Validation;

/// <summary>
/// Validates bank connection data — IBAN and Czech domestic account numbers.
///
/// WHY this lives in Fakvio.Domain:
/// the same rules must be applied in three different layers, and a duplicated checksum
/// algorithm is exactly how those layers silently drift apart (issue #154):
/// - Fakvio.UI.Shared      — inline validation in BankAccountDialog, so the user is told
///                           immediately, in the form where the value is typed.
/// - Fakvio.Infrastructure — ClientService refuses to store an unusable value.
/// - Fakvio.Infrastructure — QrPaymentService refuses to build a QR code out of data
///                           that no banking app would be able to pay.
/// Fakvio.Domain is the only project all three already reference.
///
/// The rules implemented here:
/// - IBAN: ISO 13616 structure plus the ISO 7064 MOD 97-10 checksum. For Czech IBANs the
///   embedded domestic account (prefix + base) is additionally checked with modulo 11,
///   because a CZ IBAN is nothing but a wrapper around that domestic account number.
/// - Czech account number: the "prefix-base/bankCode" format defined by CNB decree
///   169/2011, with the modulo 11 weighted checksum on both the prefix and the base.
///
/// Deliberately NOT validated: free-form foreign account numbers. The BankAccount entity
/// explicitly allows them and there is no universal checksum for them. They are accepted
/// on write, but they cannot produce a QR payment code — only an IBAN or a Czech account
/// number can.
/// </summary>
public static class BankAccountValidator
{
    /// <summary>
    /// Weight of each digit position in the Czech modulo 11 checksum, indexed from the
    /// RIGHTMOST digit (index 0). The value is simply 2^index mod 11, which is why the same
    /// array serves both the 6-digit prefix and the 10-digit account base.
    /// </summary>
    private static readonly int[] Mod11Weights = [1, 2, 4, 8, 5, 10, 9, 7, 3, 6];

    /// <summary>Shortest IBAN in use worldwide (Norway).</summary>
    private const int MinIbanLength = 15;

    /// <summary>Longest IBAN permitted by ISO 13616.</summary>
    private const int MaxIbanLength = 34;

    /// <summary>Czech IBAN: CZ + 2 check digits + 4 bank code + 6 prefix + 10 account base.</summary>
    private const int CzechIbanLength = 24;

    /// <summary>Index of the 6-digit account prefix inside a Czech IBAN.</summary>
    private const int CzechIbanPrefixOffset = 8;

    /// <summary>Index of the 10-digit account base inside a Czech IBAN.</summary>
    private const int CzechIbanAccountBaseOffset = 14;

    private const string CzechCountryCode = "CZ";
    private const int CzechBankCodeLength = 4;
    private const int MaxCzechPrefixLength = 6;
    private const int MinCzechAccountBaseLength = 2;
    private const int MaxCzechAccountBaseLength = 10;

    /// <summary>
    /// Canonical form of an IBAN: only ASCII letters and digits, upper-cased.
    ///
    /// Users habitually paste IBANs with spaces or dashes ("CZ65 0800 0000 ..."), and the
    /// grouped form is what banks print. Stripping everything that is not a letter or a
    /// digit is more robust than replacing a fixed list of separators, because it also
    /// removes non-breaking spaces copied out of PDFs.
    /// This is the form that goes into the SPD "ACC" attribute and into the checksum.
    /// </summary>
    public static string NormalizeIban(string iban) =>
        new(iban.Where(char.IsAsciiLetterOrDigit).Select(char.ToUpperInvariant).ToArray());

    /// <summary>
    /// Validates an IBAN. Returns true when the value can be used as a payment destination.
    /// </summary>
    /// <param name="iban">Raw user input; spaces and dashes are tolerated.</param>
    /// <param name="error">Human-readable reason for rejection, or null when the IBAN is valid.</param>
    public static bool TryValidateIban(string? iban, [NotNullWhen(false)] out string? error)
    {
        if (string.IsNullOrWhiteSpace(iban))
        {
            error = "IBAN is empty.";
            return false;
        }

        var normalized = NormalizeIban(iban);

        if (normalized.Length is < MinIbanLength or > MaxIbanLength)
        {
            error = $"IBAN '{normalized}' has {normalized.Length} characters; " +
                    $"a valid IBAN has between {MinIbanLength} and {MaxIbanLength}.";
            return false;
        }

        // Structure: two country letters, two check digits, then letters and digits only.
        if (!char.IsAsciiLetterUpper(normalized[0]) || !char.IsAsciiLetterUpper(normalized[1]) ||
            !char.IsAsciiDigit(normalized[2]) || !char.IsAsciiDigit(normalized[3]))
        {
            error = $"IBAN '{normalized}' does not have the required format " +
                    "(two country letters, two check digits, then letters and digits).";
            return false;
        }

        if (!HasValidIbanChecksum(normalized))
        {
            error = $"IBAN '{normalized}' has an invalid check digit — it contains a typo.";
            return false;
        }

        // A Czech IBAN embeds the domestic account number, so it must satisfy the domestic
        // checksum too. Without this check, a mod-97-correct but domestically nonsensical
        // IBAN would still be accepted and would still produce an unpayable QR code.
        if (normalized.StartsWith(CzechCountryCode, StringComparison.Ordinal))
        {
            return TryValidateCzechIbanBody(normalized, out error);
        }

        error = null;
        return true;
    }

    /// <summary>
    /// Tells whether the value has the shape of a Czech domestic account number
    /// ("[prefix-]base/bankCode", digits only, 4-digit bank code).
    ///
    /// Used to decide WHICH rules apply: only Czech-shaped values are checked with
    /// modulo 11, anything else is treated as a free-form foreign account.
    /// </summary>
    public static bool HasCzechAccountNumberFormat(string? accountNumber) =>
        TrySplitCzechAccountNumber(accountNumber, out _, out _, out _);

    /// <summary>
    /// Validates a Czech domestic account number, including the modulo 11 checksum.
    /// </summary>
    /// <param name="accountNumber">Raw user input, e.g. "19-2000145399/0800".</param>
    /// <param name="error">Human-readable reason for rejection, or null when valid.</param>
    public static bool TryValidateCzechAccountNumber(
        string? accountNumber, [NotNullWhen(false)] out string? error)
    {
        if (!TrySplitCzechAccountNumber(accountNumber, out var prefix, out var accountBase, out _))
        {
            error = $"Bank account '{accountNumber}' is not a Czech account number — " +
                    "the expected format is 'prefix-number/bankCode', for example '19-2000145399/0800'.";
            return false;
        }

        // An all-zero base passes the checksum but is not an account anybody can pay to.
        if (accountBase.All(digit => digit == '0'))
        {
            error = $"Bank account '{accountNumber}' has a zero account number.";
            return false;
        }

        if (!IsMod11Valid(prefix))
        {
            error = $"Bank account '{accountNumber}' has an invalid prefix — " +
                    "it fails the modulo 11 check required for Czech accounts.";
            return false;
        }

        if (!IsMod11Valid(accountBase))
        {
            error = $"Bank account '{accountNumber}' is not a valid Czech account number — " +
                    "it fails the modulo 11 check, which means it contains a typo.";
            return false;
        }

        error = null;
        return true;
    }

    /// <summary>
    /// Validates whatever the user stored in BankAccount.AccountNumber.
    ///
    /// The rule is shape-driven on purpose (see the class comment): a Czech-shaped value
    /// must be a correct Czech account, anything else is accepted as a foreign account.
    /// This rejects exactly the values that would silently produce an unpayable QR code,
    /// without blocking legitimate international accounts.
    /// </summary>
    public static bool TryValidateAccountNumber(
        string? accountNumber, [NotNullWhen(false)] out string? error)
    {
        if (string.IsNullOrWhiteSpace(accountNumber))
        {
            error = "Bank account number is empty.";
            return false;
        }

        if (HasCzechAccountNumberFormat(accountNumber))
        {
            return TryValidateCzechAccountNumber(accountNumber, out error);
        }

        error = null;
        return true;
    }

    /// <summary>
    /// Splits "[prefix-]base/bankCode" into its three parts.
    /// Returns false when the value does not have the Czech shape at all.
    /// The prefix is returned as an empty string when the account has none.
    /// </summary>
    private static bool TrySplitCzechAccountNumber(
        string? accountNumber, out string prefix, out string accountBase, out string bankCode)
    {
        prefix = string.Empty;
        accountBase = string.Empty;
        bankCode = string.Empty;

        if (string.IsNullOrWhiteSpace(accountNumber))
        {
            return false;
        }

        var compact = accountNumber.Replace(" ", string.Empty).Trim();

        var slashParts = compact.Split('/');
        if (slashParts.Length != 2)
        {
            return false;
        }

        bankCode = slashParts[1];
        if (bankCode.Length != CzechBankCodeLength || !bankCode.All(char.IsAsciiDigit))
        {
            return false;
        }

        var dashParts = slashParts[0].Split('-');
        if (dashParts.Length > 2)
        {
            return false;
        }

        if (dashParts.Length == 2)
        {
            prefix = dashParts[0];
            accountBase = dashParts[1];

            if (prefix.Length is 0 or > MaxCzechPrefixLength || !prefix.All(char.IsAsciiDigit))
            {
                return false;
            }
        }
        else
        {
            accountBase = dashParts[0];
        }

        return accountBase.Length is >= MinCzechAccountBaseLength and <= MaxCzechAccountBaseLength
               && accountBase.All(char.IsAsciiDigit);
    }

    /// <summary>
    /// Czech modulo 11 weighted checksum. An empty string (a missing prefix) is valid.
    /// </summary>
    private static bool IsMod11Valid(string digits)
    {
        if (digits.Length == 0)
        {
            return true;
        }

        var sum = 0;
        for (var positionFromRight = 0; positionFromRight < digits.Length; positionFromRight++)
        {
            var digit = digits[digits.Length - 1 - positionFromRight] - '0';
            sum += digit * Mod11Weights[positionFromRight];
        }

        return sum % 11 == 0;
    }

    /// <summary>
    /// ISO 7064 MOD 97-10 checksum: move the first four characters to the end, translate
    /// letters into numbers (A=10 ... Z=35), and require the resulting number mod 97 to be 1.
    /// That number is far too large for a long, so the remainder is folded character by
    /// character — a letter contributes two decimal digits, hence the multiplier of 100.
    /// </summary>
    private static bool HasValidIbanChecksum(string normalizedIban)
    {
        var rearranged = string.Concat(normalizedIban.AsSpan(4), normalizedIban.AsSpan(0, 4));

        var remainder = 0;
        foreach (var character in rearranged)
        {
            if (char.IsAsciiDigit(character))
            {
                remainder = (remainder * 10 + (character - '0')) % 97;
            }
            else
            {
                remainder = (remainder * 100 + (character - 'A' + 10)) % 97;
            }
        }

        return remainder == 1;
    }

    /// <summary>
    /// Checks the domestic part of a Czech IBAN: CZkk BBBB PPPPPP AAAAAAAAAA.
    /// Called only after the generic IBAN checks have already passed.
    /// </summary>
    private static bool TryValidateCzechIbanBody(
        string normalizedIban, [NotNullWhen(false)] out string? error)
    {
        if (normalizedIban.Length != CzechIbanLength)
        {
            error = $"IBAN '{normalizedIban}' is not a valid Czech IBAN — " +
                    $"a CZ IBAN has exactly {CzechIbanLength} characters.";
            return false;
        }

        if (!normalizedIban.Skip(4).All(char.IsAsciiDigit))
        {
            error = $"IBAN '{normalizedIban}' is not a valid Czech IBAN — " +
                    "everything after the check digits must be numeric.";
            return false;
        }

        var prefix = normalizedIban.Substring(CzechIbanPrefixOffset, MaxCzechPrefixLength);
        var accountBase = normalizedIban.Substring(CzechIbanAccountBaseOffset, MaxCzechAccountBaseLength);

        if (accountBase.All(digit => digit == '0'))
        {
            error = $"IBAN '{normalizedIban}' carries a zero account number.";
            return false;
        }

        if (!IsMod11Valid(prefix) || !IsMod11Valid(accountBase))
        {
            error = $"IBAN '{normalizedIban}' carries an account number that fails the Czech " +
                    "modulo 11 check — it contains a typo.";
            return false;
        }

        error = null;
        return true;
    }
}

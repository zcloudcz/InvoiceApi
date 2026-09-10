namespace Fakvio.Domain.Validation;

/// <summary>
/// Validates IBAN and Czech domestic bank account numbers before they are saved or used to
/// generate a payment QR code (issue #154). Format alone is not enough — a string that merely
/// "looks like" an IBAN or account number can still be unscannable or point at no real account,
/// and previously reached SpdIntegrator / paylibo.com unvalidated.
/// </summary>
public static class CzechBankAccountValidator
{
    /// <summary>
    /// Weights for the 10-digit account number part, applied from the RIGHTMOST digit
    /// (weights[0] multiplies the last digit, weights[1] the second-to-last, and so on).
    /// Source: ČNB modulo-11 check — https://overucet.cz/modulo-11-kontrola-cisla-uctu.php
    /// </summary>
    private static readonly int[] AccountWeights = { 1, 2, 4, 8, 5, 10, 9, 7, 3, 6 };

    /// <summary>
    /// Weights for the (optional) 6-digit prefix part — the first six of <see cref="AccountWeights"/>,
    /// same right-to-left application. An account with no prefix is equivalent to prefix "000000",
    /// which always passes (every weighted digit is zero).
    /// </summary>
    private static readonly int[] PrefixWeights = { 1, 2, 4, 8, 5, 10 };

    /// <summary>
    /// Validates an IBAN per ISO 13616 (structure) + ISO 7064 MOD 97-10 (check digits).
    /// Accepts spaces and dashes as formatting (e.g. "CZ65 0800 ..."); rejects everything else
    /// that is not a correctly checksummed IBAN. Does not restrict to Czech IBANs — the SPD/QR
    /// Platba flow can carry any country's IBAN.
    /// </summary>
    public static bool IsValidIban(string? iban)
    {
        if (string.IsNullOrWhiteSpace(iban))
            return false;

        var cleaned = iban.Replace(" ", "").Replace("-", "").ToUpperInvariant();

        // ISO 13616: 2 letters (country) + 2 digits (check) + up to 30 alphanumeric (BBAN).
        if (cleaned.Length < 5 || cleaned.Length > 34)
            return false;
        if (!char.IsLetter(cleaned[0]) || !char.IsLetter(cleaned[1]))
            return false;
        if (!char.IsDigit(cleaned[2]) || !char.IsDigit(cleaned[3]))
            return false;
        for (var i = 4; i < cleaned.Length; i++)
        {
            if (!char.IsLetterOrDigit(cleaned[i]))
                return false;
        }

        // Move the first 4 characters (country + check digits) to the end, then reduce to a
        // digit string by replacing every letter with its two-digit value (A=10 … Z=35), and
        // compute the whole thing mod 97 by streaming — the number is far too large for long/decimal.
        var rearranged = cleaned[4..] + cleaned[..4];
        long remainder = 0;
        foreach (var c in rearranged)
        {
            if (char.IsDigit(c))
            {
                remainder = (remainder * 10 + (c - '0')) % 97;
            }
            else
            {
                var value = c - 'A' + 10; // two-digit value, 10-35
                remainder = (remainder * 10 + value / 10) % 97;
                remainder = (remainder * 10 + value % 10) % 97;
            }
        }

        return remainder == 1;
    }

    /// <summary>
    /// Validates a Czech domestic account number in "[prefix-]account/bankCode" format
    /// (e.g. "19-2000145002/0800" or "2000145002/0800") using the ČNB modulo-11 check on both
    /// the prefix and the account part, plus a 4-digit numeric bank code.
    /// </summary>
    public static bool IsValidCzechAccountNumber(string? accountNumber)
    {
        if (string.IsNullOrWhiteSpace(accountNumber))
            return false;

        var cleaned = accountNumber.Replace(" ", "");

        var slashIndex = cleaned.IndexOf('/');
        if (slashIndex < 0)
            return false; // Czech format always carries a bank code after '/'.

        var bankCode = cleaned[(slashIndex + 1)..];
        if (bankCode.Length != 4 || !bankCode.All(char.IsDigit))
            return false;

        var numberPart = cleaned[..slashIndex];
        var dashIndex = numberPart.IndexOf('-');
        var prefix = dashIndex >= 0 ? numberPart[..dashIndex] : string.Empty;
        var account = dashIndex >= 0 ? numberPart[(dashIndex + 1)..] : numberPart;

        // The account must actually contain digits — "/0800" or "-/0800" is not a real account.
        if (account.Length == 0 || account.All(c => c == '0'))
            return false;

        if (!IsValidWeightedPart(prefix, PrefixWeights) || !IsValidWeightedPart(account, AccountWeights))
            return false;

        return true;
    }

    /// <summary>
    /// True when EITHER the IBAN or the Czech domestic account number is present and passes its
    /// own checksum — i.e. there is at least one usable payment destination to build a QR from.
    /// </summary>
    public static bool HasUsableBankConnection(string? iban, string? czechAccountNumber)
        => IsValidIban(iban) || IsValidCzechAccountNumber(czechAccountNumber);

    /// <summary>
    /// Sums <paramref name="digits"/> (rightmost digit first) against <paramref name="weights"/>
    /// and checks divisibility by 11. An empty <paramref name="digits"/> (no prefix supplied) is
    /// valid by definition — equivalent to an all-zero prefix, whose weighted sum is always 0.
    /// </summary>
    private static bool IsValidWeightedPart(string digits, int[] weights)
    {
        if (digits.Length > weights.Length || !digits.All(char.IsDigit))
            return false;

        var sum = 0;
        for (var i = 0; i < digits.Length; i++)
        {
            var digit = digits[digits.Length - 1 - i] - '0';
            sum += digit * weights[i];
        }

        return sum % 11 == 0;
    }
}

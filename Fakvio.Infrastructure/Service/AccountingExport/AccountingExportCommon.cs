using System.Globalization;
using Fakvio.Domain.Entities;
using Fakvio.Infrastructure.Service.Ubl;

namespace Fakvio.Infrastructure.Service.AccountingExport;

/// <summary>
/// Shared helpers used by all three <see cref="Fakvio.Application.Service.IAccountingExporter"/>
/// implementations (Pohoda, Money S3, ABRA Flexi) — VAT rate bucketing, bank account parsing,
/// culture-invariant decimal formatting. Kept here instead of duplicated per-mapper, same spirit
/// as IsdocMapper's private helpers but shared because all three exports need the same facts.
/// </summary>
internal static class AccountingExportCommon
{
    /// <summary>
    /// Czech VAT-rate bucket used by the exporters. The export targets the rates valid since
    /// 1 Jan 2024: 21 % (základní), 12 % (snížená) and 0 %. Any other rate (e.g. the pre-2024 10 % /
    /// 15 % rates, or non-CZ rates) is NOT mapped — such documents are skipped by
    /// <see cref="AllRatesSupported"/> so the importer never receives a wrong VAT classification.
    /// </summary>
    internal enum VatBucket { None, Low, High }

    internal static bool IsSupportedRate(decimal ratePercentage) => ratePercentage is 0m or 12m or 21m;

    internal static bool AllRatesSupported(IEnumerable<decimal> ratePercentages) => ratePercentages.All(IsSupportedRate);

    internal static VatBucket ClassifyVatRate(decimal ratePercentage) =>
        ratePercentage == 21m ? VatBucket.High
        : ratePercentage == 12m ? VatBucket.Low
        : VatBucket.None;

    internal static bool IsHomeCurrency(string? currencyCode) =>
        string.IsNullOrEmpty(currencyCode) || currencyCode.Equals("CZK", StringComparison.OrdinalIgnoreCase);

    /// <summary>Cuts a string to the importer's maximum field length (null-safe).</summary>
    internal static string Truncate(string? value, int maxLength) =>
        value is null ? string.Empty : value.Length <= maxLength ? value : value[..maxLength];

    /// <summary>Parses the Czech "number/bankCode" bank account format into its two parts.</summary>
    internal static (string AccountNumber, string BankCode) ParseBankAccount(string? bankAccount)
    {
        if (string.IsNullOrWhiteSpace(bankAccount)) return (string.Empty, string.Empty);
        var idx = bankAccount.IndexOf('/');
        return idx < 0 ? (bankAccount, string.Empty) : (bankAccount[..idx], bankAccount[(idx + 1)..]);
    }

    internal static string FormatDecimal(decimal value) => value.ToString("F2", CultureInfo.InvariantCulture);

    internal static string FormatDate(DateTime? date) =>
        date.HasValue ? date.Value.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) : string.Empty;

    /// <summary>Primary (or first) address of a client, same fallback rule used by IsdocMapper/UblMapper.</summary>
    internal static Address? PrimaryAddress(Client? client) =>
        client?.Address?.FirstOrDefault(a => a.IsPrimary) ?? client?.Address?.FirstOrDefault();

    /// <summary>ISO 3166-1 alpha-2 country code, reusing the same table as ISDOC/UBL exports.</summary>
    internal static string CountryIso2(Address? address) => UblCodes.CountryToIso2(address?.Country) ?? "CZ";
}

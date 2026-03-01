using System.Globalization;
using System.Web;

namespace Fakvio.Application.QrPayment;

/// <summary>
/// Options for the paylibo.com QR payment code generator API.
///
/// The paylibo API accepts Czech bank account details in domestic format
/// (accountPrefix-accountNumber/bankCode) and generates a valid QR Platba
/// PNG image. This is the same approach used by Monarc.Core.
///
/// API endpoint: https://api.paylibo.com/paylibo/generator/czech/image
///
/// Each property maps to a query parameter in the API URL.
/// The ToString() method builds the complete query string using reflection,
/// matching the Monarc.Core QRPaymentOptions pattern exactly.
/// </summary>
public class PayliboQrOptions
{
    /// <summary>
    /// Bank account prefix (part before the dash in Czech format, e.g., "19" in "19-1234567890/0100").
    /// Optional — not all accounts have a prefix.
    /// </summary>
    public string? accountPrefix { get; set; }

    /// <summary>
    /// Bank account number (part after the dash or the whole number if no prefix).
    /// For "19-1234567890/0100" → accountNumber = "1234567890".
    /// For "1342333010/3030" → accountNumber = "1342333010".
    /// </summary>
    public string? accountNumber { get; set; }

    /// <summary>
    /// Czech bank code (part after the slash, e.g., "0100" for Komerční banka).
    /// </summary>
    public string? bankCode { get; set; }

    /// <summary>
    /// Payment amount in CZK or other currency.
    /// Formatted with invariant culture (dot as decimal separator).
    /// </summary>
    public decimal amount { get; set; }

    /// <summary>
    /// ISO 4217 currency code (e.g., "CZK", "EUR").
    /// </summary>
    public string? currency { get; set; }

    /// <summary>
    /// Variabilní symbol — Czech payment identifier (max 10 digits).
    /// </summary>
    public string? vs { get; set; }

    /// <summary>
    /// Konstantní symbol — constant symbol for payment categorization.
    /// </summary>
    public string? ks { get; set; }

    /// <summary>
    /// Specifický symbol — specific symbol for additional identification.
    /// </summary>
    public string? ss { get; set; }

    /// <summary>
    /// Internal identifier (not displayed in banking app).
    /// </summary>
    public string? identifier { get; set; }

    /// <summary>
    /// Due date in ISO 8601 short format (YYYY-MM-DD).
    /// </summary>
    public DateOnly? date { get; set; }

    /// <summary>
    /// Payment message / description shown in the banking app.
    /// </summary>
    public string? message { get; set; }

    /// <summary>
    /// Whether to compress the QR code data. Default: false.
    /// </summary>
    public bool compress { get; set; } = false;

    /// <summary>
    /// Whether to include paylibo branding in the QR image. Default: true.
    /// </summary>
    public bool branding { get; set; } = true;

    /// <summary>
    /// QR code image size in pixels (module size multiplier). Default: 200.
    /// </summary>
    public int size { get; set; } = 200;

    /// <summary>
    /// Builds the URL query string from all properties using reflection.
    /// Matches the Monarc.Core QRPaymentOptions.ToString() pattern:
    /// - decimal values use InvariantCulture (dot separator)
    /// - DateOnly values use ISO 8601 format (yyyy-MM-dd)
    /// - null/empty string values are skipped
    /// - all values are URL-encoded
    /// </summary>
    public override string ToString()
    {
        var parts = new List<string>();

        foreach (var prop in GetType().GetProperties())
        {
            var value = prop.GetValue(this);
            if (value == null) continue;

            if (prop.PropertyType == typeof(decimal))
            {
                // Decimal uses invariant culture (dot separator) — "6.05" not "6,05"
                parts.Add($"{prop.Name}={HttpUtility.UrlEncode(((decimal)value).ToString(CultureInfo.InvariantCulture))}");
            }
            else if (prop.PropertyType == typeof(DateOnly?))
            {
                // DateOnly uses ISO 8601 short format
                var dateValue = (DateOnly?)value;
                if (dateValue.HasValue)
                {
                    parts.Add($"{prop.Name}={HttpUtility.UrlEncode(dateValue.Value.ToString("yyyy-MM-dd"))}");
                }
            }
            else
            {
                var strValue = value.ToString();
                if (!string.IsNullOrEmpty(strValue))
                {
                    parts.Add($"{prop.Name}={HttpUtility.UrlEncode(strValue)}");
                }
            }
        }

        return string.Join("&", parts);
    }
}

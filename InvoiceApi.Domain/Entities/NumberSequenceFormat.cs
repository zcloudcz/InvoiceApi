using InvoiceApi.Domain.Common;

namespace InvoiceApi.Domain.Entities;

/// <summary>
/// Defines the format pattern for generating invoice/credit note numbers
/// Examples: "yyyyNNN" -> 2024001, "yyMMNNN" -> 240101, "yy-NNN" -> 24-001
/// This allows flexible number formatting that users can customize
/// </summary>
public class NumberSequenceFormat : BaseEntity
{
    /// <summary>
    /// Name/description of this format
    /// Example: "Standard yearly format", "Monthly format"
    /// </summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>
    /// The format pattern using placeholders:
    /// - yyyy = 4-digit year (2024)
    /// - yy = 2-digit year (24)
    /// - MM = 2-digit month (01-12)
    /// - NNN = counter with specified number of digits (can be 1-10 N's)
    /// - Any other characters are used as-is (separators like -, /)
    /// Example patterns:
    ///   "yyyyNNN" -> 2024001
    ///   "yyMMNNN" -> 2401001
    ///   "INV-yy-NNNN" -> INV-24-0001
    /// </summary>
    public string FormatPattern { get; set; } = string.Empty;

    /// <summary>
    /// How many digits should the counter part (N's) have
    /// Example: 3 means numbers like 001, 002, 003
    ///          5 means numbers like 00001, 00002
    /// This is extracted from counting N's in FormatPattern
    /// </summary>
    public int CounterDigits { get; set; }

    /// <summary>
    /// Whether this format resets counter annually (every year)
    /// If true: Jan 2024 = 001, Jan 2025 = 001 (reset)
    /// If false: Jan 2024 = 001, Jan 2025 = 150 (continues)
    /// Only relevant if format contains year (yyyy or yy)
    /// </summary>
    public bool ResetsYearly { get; set; }

    /// <summary>
    /// Whether this format resets counter monthly (every month)
    /// If true: Jan = 001, Feb = 001 (reset each month)
    /// If false: Jan = 001, Feb = 032 (continues)
    /// Only relevant if format contains month (MM)
    /// </summary>
    public bool ResetsMonthly { get; set; }

    /// <summary>
    /// Is this format currently active and available for use
    /// Inactive formats cannot be assigned to new number sequences
    /// </summary>
    public bool IsActive { get; set; } = true;
}

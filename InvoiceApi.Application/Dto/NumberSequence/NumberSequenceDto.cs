using InvoiceApi.Domain.Enums;

namespace InvoiceApi.Application.Dto.NumberSequence;

/// <summary>
/// DTO for number sequence data
/// </summary>
public class NumberSequenceDto
{
    public long Id { get; set; }
    public string Name { get; set; } = string.Empty;
    public EDocumentType DocumentType { get; set; }
    public string? Prefix { get; set; }
    public string? Suffix { get; set; }
    public int CurrentNumber { get; set; }
    public bool IsDefault { get; set; }
    public long NumberSequenceFormatId { get; set; }
    public NumberSequenceFormatDto NumberSequenceFormat { get; set; } = null!;
    public int? CurrentYear { get; set; }
    public int? CurrentMonth { get; set; }
    public bool IsActive { get; set; }
}

/// <summary>
/// DTO for number sequence format data
/// </summary>
public class NumberSequenceFormatDto
{
    public long Id { get; set; }
    public string Name { get; set; } = string.Empty;
    public string FormatPattern { get; set; } = string.Empty;
    public int CounterDigits { get; set; }
    public bool ResetsYearly { get; set; }
    public bool ResetsMonthly { get; set; }
    public bool IsActive { get; set; }
}

/// <summary>
/// DTO for creating a number sequence
/// </summary>
public class CreateNumberSequenceDto
{
    public string Name { get; set; } = string.Empty;
    public EDocumentType DocumentType { get; set; }
    public string? Prefix { get; set; }
    public string? Suffix { get; set; }
    public int StartingNumber { get; set; } = 1;
    public bool IsDefault { get; set; } = false;
    public long NumberSequenceFormatId { get; set; }
}

/// <summary>
/// DTO for creating a number sequence format
/// </summary>
public class CreateNumberSequenceFormatDto
{
    public string Name { get; set; } = string.Empty;
    public string FormatPattern { get; set; } = string.Empty;
}

/// <summary>
/// DTO for updating an existing number sequence format.
/// Only Name and FormatPattern are editable — CounterDigits, ResetsYearly,
/// and ResetsMonthly are derived from the pattern and cannot be changed independently.
/// </summary>
public class UpdateNumberSequenceFormatDto
{
    /// <summary>
    /// Human-readable name for this format
    /// </summary>
    public string? Name { get; set; }

    /// <summary>
    /// Format pattern (e.g., "yyyyNNNN", "yyMMNNN").
    /// If changed, must still be a valid pattern containing at least one 'N'.
    /// </summary>
    public string? FormatPattern { get; set; }
}

/// <summary>
/// DTO for updating an existing number sequence.
/// Only safe fields are editable — DocumentType, FormatId, CurrentNumber
/// are NOT editable because changing them would break existing numbering.
/// </summary>
public class UpdateNumberSequenceDto
{
    /// <summary>
    /// Human-readable name for this sequence
    /// </summary>
    public string? Name { get; set; }

    /// <summary>
    /// Prefix prepended to generated numbers (e.g., "INV-")
    /// </summary>
    public string? Prefix { get; set; }

    /// <summary>
    /// Suffix appended to generated numbers
    /// </summary>
    public string? Suffix { get; set; }
}

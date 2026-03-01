using Fakvio.Domain.Common;
using Fakvio.Domain.Enums;

namespace Fakvio.Domain.Entities;

/// <summary>
/// Represents a number sequence for generating invoice/credit note numbers
/// Each sequence has its own counter and can have different format
/// Example: You might have separate sequences for invoices and credit notes,
///          or different sequences for different branches/departments
/// </summary>
public class NumberSequence : BaseEntity
{
    /// <summary>
    /// Name/description of this sequence
    /// Example: "Main invoice sequence", "Export invoices", "Credit notes"
    /// </summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>
    /// Type of documents this sequence is used for
    /// Each sequence can only generate numbers for one document type
    /// </summary>
    public EDocumentType DocumentType { get; set; }

    /// <summary>
    /// Optional prefix added before the generated number
    /// Example: "INV-" results in "INV-2024001"
    /// Can be empty if not needed
    /// </summary>
    public string? Prefix { get; set; }

    /// <summary>
    /// Optional suffix added after the generated number
    /// Example: "-CZ" results in "2024001-CZ"
    /// Can be empty if not needed
    /// </summary>
    public string? Suffix { get; set; }

    /// <summary>
    /// Current counter value - the next document will get this number
    /// This increments with each generated document
    /// Example: If CurrentNumber = 15, next invoice will be 15, then this becomes 16
    /// </summary>
    public int CurrentNumber { get; set; } = 1;

    /// <summary>
    /// Is this the default sequence for this DocumentType?
    /// There can be only ONE default sequence per DocumentType
    /// Default sequence is used when client doesn't have specific sequence assigned
    /// </summary>
    public bool IsDefault { get; set; } = false;

    /// <summary>
    /// Foreign key to NumberSequenceFormat
    /// Defines the pattern for generating numbers (yyyyNNN, etc.)
    /// </summary>
    public long NumberSequenceFormatId { get; set; }

    /// <summary>
    /// Navigation property to the format definition
    /// Contains the pattern and rules for number generation
    /// </summary>
    public NumberSequenceFormat NumberSequenceFormat { get; set; } = null!;

    /// <summary>
    /// Year for which this counter is valid
    /// If format ResetsYearly=true, counter resets each year
    /// Null means counter never resets based on year
    /// </summary>
    public int? CurrentYear { get; set; }

    /// <summary>
    /// Month for which this counter is valid (1-12)
    /// If format ResetsMonthly=true, counter resets each month
    /// Null means counter never resets based on month
    /// </summary>
    public int? CurrentMonth { get; set; }

    /// <summary>
    /// Is this sequence currently active and available for use
    /// Inactive sequences cannot be used for new documents
    /// </summary>
    public bool IsActive { get; set; } = true;

    /// <summary>
    /// Concurrency token for optimistic concurrency control.
    /// PostgreSQL uses the hidden system column "xmin" — a 32-bit transaction ID that is
    /// automatically updated by the database engine on every INSERT/UPDATE.
    /// EF Core checks this value on every update — if another request modified
    /// the row since we read it, SaveChanges throws DbUpdateConcurrencyException.
    /// This prevents race conditions when two requests try to generate a
    /// document number simultaneously (duplicate number prevention).
    /// Configured as .UseXminAsConcurrencyToken() in TenantDbContext.
    /// </summary>
    public uint RowVersion { get; set; }
}

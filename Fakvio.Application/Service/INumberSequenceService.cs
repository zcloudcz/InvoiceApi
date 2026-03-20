using Fakvio.Contracts.Dto.NumberSequence;
using Fakvio.Domain.Enums;

namespace Fakvio.Application.Service;

/// <summary>
/// Service for managing number sequences and formats
/// Handles number generation logic for invoices and credit notes
/// </summary>
public interface INumberSequenceService
{
    // NumberSequenceFormat operations

    /// <summary>
    /// Gets all number sequence formats
    /// </summary>
    /// <param name="includeInactive">Include inactive formats</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>List of formats</returns>
    Task<List<NumberSequenceFormatDto>> GetAllFormatsAsync(bool includeInactive = false, CancellationToken cancellationToken = default);

    /// <summary>
    /// Gets a specific format by ID
    /// </summary>
    /// <param name="formatId">Format ID</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>Format data or null if not found</returns>
    Task<NumberSequenceFormatDto?> GetFormatByIdAsync(long formatId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Creates a new number sequence format
    /// </summary>
    /// <param name="createDto">Format data</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>Created format</returns>
    Task<NumberSequenceFormatDto> CreateFormatAsync(CreateNumberSequenceFormatDto createDto, CancellationToken cancellationToken = default);

    /// <summary>
    /// Updates an existing number sequence format.
    /// Only Name and FormatPattern are editable.
    /// </summary>
    /// <param name="formatId">Format ID to update</param>
    /// <param name="updateDto">Fields to update</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>Updated format or null if not found</returns>
    Task<NumberSequenceFormatDto?> UpdateFormatAsync(long formatId, UpdateNumberSequenceFormatDto updateDto, CancellationToken cancellationToken = default);

    // NumberSequence operations

    /// <summary>
    /// Gets all number sequences
    /// </summary>
    /// <param name="documentType">Filter by document type (null = all)</param>
    /// <param name="includeInactive">Include inactive sequences</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>List of sequences</returns>
    Task<List<NumberSequenceDto>> GetAllSequencesAsync(
        EDocumentType? documentType = null,
        bool includeInactive = false,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Gets a specific sequence by ID
    /// </summary>
    /// <param name="sequenceId">Sequence ID</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>Sequence data or null if not found</returns>
    Task<NumberSequenceDto?> GetSequenceByIdAsync(long sequenceId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Gets the default sequence for a document type
    /// </summary>
    /// <param name="documentType">Document type</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>Default sequence or null if not configured</returns>
    Task<NumberSequenceDto?> GetDefaultSequenceAsync(EDocumentType documentType, CancellationToken cancellationToken = default);

    /// <summary>
    /// Creates a new number sequence
    /// </summary>
    /// <param name="createDto">Sequence data</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>Created sequence</returns>
    Task<NumberSequenceDto> CreateSequenceAsync(CreateNumberSequenceDto createDto, CancellationToken cancellationToken = default);

    /// <summary>
    /// Updates an existing number sequence.
    /// Only Name, Prefix, and Suffix are editable (safe fields).
    /// </summary>
    /// <param name="sequenceId">Sequence ID to update</param>
    /// <param name="updateDto">Fields to update</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>Updated sequence or null if not found</returns>
    Task<NumberSequenceDto?> UpdateSequenceAsync(long sequenceId, UpdateNumberSequenceDto updateDto, CancellationToken cancellationToken = default);

    /// <summary>
    /// Sets a sequence as default for its document type
    /// Unsets any other default sequence for that document type
    /// </summary>
    /// <param name="sequenceId">Sequence ID</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>Updated sequence or null if not found</returns>
    Task<NumberSequenceDto?> SetAsDefaultAsync(long sequenceId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Deactivates a number sequence
    /// Cannot be deactivated if it's the default sequence
    /// </summary>
    /// <param name="sequenceId">Sequence ID</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>True if deactivated, false if not found</returns>
    Task<bool> DeactivateSequenceAsync(long sequenceId, CancellationToken cancellationToken = default);

    // Number generation

    /// <summary>
    /// Generates the next document number using a specific sequence
    /// Automatically increments the counter and handles year/month resets
    /// </summary>
    /// <param name="sequenceId">Sequence ID to use</param>
    /// <param name="issueDate">Issue date (for year/month in format)</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>Generated document number</returns>
    Task<string> GenerateNextNumberAsync(long sequenceId, DateTime issueDate, CancellationToken cancellationToken = default);

    /// <summary>
    /// Generates the next document number using default sequence for document type
    /// </summary>
    /// <param name="documentType">Document type</param>
    /// <param name="issueDate">Issue date (for year/month in format)</param>
    /// <param name="prefix">Optional custom prefix (overrides sequence prefix)</param>
    /// <param name="suffix">Optional custom suffix (overrides sequence suffix)</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>Generated document number</returns>
    Task<string> GenerateNextNumberForDocumentTypeAsync(
        EDocumentType documentType,
        DateTime issueDate,
        string? prefix = null,
        string? suffix = null,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Previews what the next number would be without incrementing the counter
    /// Useful for showing users what number will be generated
    /// </summary>
    /// <param name="sequenceId">Sequence ID</param>
    /// <param name="issueDate">Issue date (for year/month in format)</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>Preview of next number</returns>
    Task<string> PreviewNextNumberAsync(long sequenceId, DateTime issueDate, CancellationToken cancellationToken = default);

    /// <summary>
    /// Previews the next document number for a given document type using the default sequence.
    /// Convenience method that resolves the default sequence and calls PreviewNextNumberAsync.
    /// Returns null if no default sequence is configured (instead of throwing).
    /// </summary>
    /// <param name="documentType">Document type (Invoice or CreditNote)</param>
    /// <param name="issueDate">Issue date for year/month placeholders</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>Preview string or null if no default sequence exists</returns>
    Task<string?> PreviewNextNumberForDocumentTypeAsync(
        EDocumentType documentType, DateTime issueDate, CancellationToken cancellationToken = default);

    /// <summary>
    /// Releases (decrements) the last generated number in a sequence.
    /// Called when a draft invoice is deleted to keep the numbering continuous.
    ///
    /// IMPORTANT: Only decrements if the current counter matches the expected value
    /// (i.e., the deleted invoice had the last number in the sequence).
    /// If another invoice was created after this one, the number cannot be released
    /// because it would create a gap.
    ///
    /// Junior note: If invoice #5 is deleted but invoice #6 already exists,
    /// we can't release #5 because #6 would become a gap. Only the LAST number
    /// in the sequence can be safely released.
    /// </summary>
    /// <param name="documentType">Document type to find the sequence for.</param>
    /// <param name="expectedCurrentNumber">
    /// The counter value we expect. Only decrements if it matches.
    /// This is the number AFTER the deleted invoice was generated
    /// (e.g., if deleted invoice was #5, expectedCurrentNumber is 6).
    /// </param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>True if the number was released, false if not (counter already moved past).</returns>
    Task<bool> TryReleaseLastNumberAsync(
        EDocumentType documentType,
        int expectedCurrentNumber,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Validates a format pattern
    /// Checks if the pattern is valid and can be used for number generation
    /// </summary>
    /// <param name="formatPattern">Pattern to validate (e.g., "yyyyNNN", "yyMMNNN")</param>
    /// <returns>True if valid, false otherwise</returns>
    bool ValidateFormatPattern(string formatPattern);

    /// <summary>
    /// Gets format pattern description/explanation
    /// </summary>
    /// <param name="formatPattern">Format pattern</param>
    /// <returns>Human-readable explanation</returns>
    string GetFormatPatternDescription(string formatPattern);
}

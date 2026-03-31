using Fakvio.Contracts.Dto.NumberSequence;
using Fakvio.Application.Service;
using Fakvio.Domain.Entities;
using Fakvio.Domain.Enums;
using Fakvio.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using System.Text.RegularExpressions;
using ZMapper;

namespace Fakvio.Infrastructure.Service;

/// <summary>
/// Implementation of number sequence service.
/// Handles all number generation logic for invoices and credit notes.
///
/// Dual-context for FORMAT operations: MasterDbContext when SysAdmin has no tenant,
/// TenantDbContext when impersonating or regular user.
/// SEQUENCE operations always use TenantDbContext (sequences are per-tenant with counters).
/// </summary>
public class NumberSequenceService : INumberSequenceService
{
    private readonly TenantDbContext _tenantContext;
    private readonly MasterDbContext _masterContext;
    private readonly ITenantResolver _tenantResolver;
    private readonly ILogger<NumberSequenceService> _logger;

    public NumberSequenceService(
        TenantDbContext tenantContext,
        MasterDbContext masterContext,
        ITenantResolver tenantResolver,
        ILogger<NumberSequenceService> logger)
    {
        _tenantContext = tenantContext;
        _masterContext = masterContext;
        _tenantResolver = tenantResolver;
        _logger = logger;
    }

    /// <summary>
    /// Whether we're operating in master context (SysAdmin without impersonation).
    /// </summary>
    private bool IsMasterContext => !_tenantResolver.GetCurrentCompanyId().HasValue;

    /// <summary>
    /// Resolves the correct NumberSequenceFormat DbSet based on context.
    /// Formats exist in both master and tenant databases.
    /// </summary>
    private DbSet<NumberSequenceFormat> FormatSet =>
        IsMasterContext ? _masterContext.NumberSequenceFormat : _tenantContext.NumberSequenceFormat;

    /// <summary>
    /// Resolves the correct DbContext for format SaveChanges operations.
    /// </summary>
    private DbContext FormatContext => IsMasterContext ? _masterContext : _tenantContext;

    /// <summary>
    /// Maps a NumberSequenceFormat entity to NumberSequenceFormatDto using ZMapper v1.1.0.
    /// ZMapper now handles all properties including inherited BaseEntity (Id).
    /// </summary>
    private static NumberSequenceFormatDto MapFormatToDto(NumberSequenceFormat entity)
    {
        return entity.ToNumberSequenceFormatDto();
    }

    /// <summary>
    /// Maps a NumberSequence entity to NumberSequenceDto using ZMapper v1.1.0.
    /// ZMapper now handles BaseEntity and nested NumberSequenceFormat automatically.
    /// </summary>
    private static NumberSequenceDto MapSequenceToDto(NumberSequence entity)
    {
        return entity.ToNumberSequenceDto();
    }

    /// <summary>
    /// Ensures the tenant has at least the default NumberSequenceFormats.
    /// Existing tenants provisioned before the fix may have empty format tables
    /// because CopyCodeTablesAsync deleted migration-seeded data when master was empty.
    /// This method seeds the 4 standard formats if the tenant has none at all.
    /// </summary>
    private async Task EnsureDefaultFormatsExistAsync(CancellationToken cancellationToken)
    {
        // Only applies to tenant context — master formats are seeded by migration
        if (IsMasterContext) return;

        var hasAnyFormat = await _tenantContext.NumberSequenceFormat.AnyAsync(cancellationToken);
        if (hasAnyFormat) return;

        _logger.LogWarning("Tenant has no NumberSequenceFormats — seeding defaults");

        // Reset the PostgreSQL sequence so new format IDs start from 1.
        // Without this, if formats were previously deleted, the sequence counter
        // continues from the last value (e.g., 5,6,7,8 instead of 1,2,3,4),
        // causing FK mismatches with existing NumberSequence records that reference old IDs.
        // Reset sequence using the tenant's schema name from the DbContext.
        // TenantDbContext.Schema is set per-request by ITenantDbContextFactory.
        var tenantSchema = _tenantContext.Schema;
        if (!string.IsNullOrEmpty(tenantSchema))
        {
            var safeName = tenantSchema.Replace("\"", "");
            await _tenantContext.Database.ExecuteSqlRawAsync(
                $"ALTER SEQUENCE IF EXISTS \"{safeName}\".\"NumberSequenceFormat_Id_seq\" RESTART WITH 1",
                cancellationToken);
        }

        var seedDate = DateTime.UtcNow;
        _tenantContext.NumberSequenceFormat.AddRange(
            new NumberSequenceFormat { Name = "Standard yearly format (yyyyNNN)", FormatPattern = "yyyyNNN", CounterDigits = 3, ResetsYearly = true, ResetsMonthly = false, IsActive = true, CreatedAt = seedDate },
            new NumberSequenceFormat { Name = "Short yearly format (yyNNN)", FormatPattern = "yyNNN", CounterDigits = 3, ResetsYearly = true, ResetsMonthly = false, IsActive = true, CreatedAt = seedDate },
            new NumberSequenceFormat { Name = "Monthly format (yyMMNNN)", FormatPattern = "yyMMNNN", CounterDigits = 3, ResetsYearly = true, ResetsMonthly = true, IsActive = true, CreatedAt = seedDate },
            new NumberSequenceFormat { Name = "Continuous format (NNNNNN)", FormatPattern = "NNNNNN", CounterDigits = 6, ResetsYearly = false, ResetsMonthly = false, IsActive = true, CreatedAt = seedDate }
        );
        await _tenantContext.SaveChangesAsync(cancellationToken);

        _logger.LogInformation("Seeded 4 default NumberSequenceFormats for tenant");
    }

    #region NumberSequenceFormat operations

    public async Task<List<NumberSequenceFormatDto>> GetAllFormatsAsync(bool includeInactive = false, CancellationToken cancellationToken = default)
    {
        // Auto-seed default formats for tenants that were provisioned with empty tables
        await EnsureDefaultFormatsExistAsync(cancellationToken);

        // AsNoTracking: read-only list — results are mapped to DTOs
        var query = FormatSet.AsNoTracking().AsQueryable();

        if (!includeInactive)
            query = query.Where(f => f.IsActive);

        var formats = await query
            .OrderBy(f => f.Name)
            .ToListAsync(cancellationToken);

        return formats.Select(f => MapFormatToDto(f)).ToList();
    }

    public async Task<NumberSequenceFormatDto?> GetFormatByIdAsync(long formatId, CancellationToken cancellationToken = default)
    {
        var format = await FormatSet.FindAsync(new object[] { formatId }, cancellationToken);
        return format == null ? null : MapFormatToDto(format);
    }

    public async Task<NumberSequenceFormatDto> CreateFormatAsync(CreateNumberSequenceFormatDto createDto, CancellationToken cancellationToken = default)
    {
        _logger.LogInformation("Creating new number sequence format: {Name}", createDto.Name);

        // Validate format pattern
        if (!ValidateFormatPattern(createDto.FormatPattern))
            throw new InvalidOperationException($"Invalid format pattern: {createDto.FormatPattern}");

        // Parse format pattern to determine settings
        var (counterDigits, resetsYearly, resetsMonthly) = ParseFormatPattern(createDto.FormatPattern);

        var format = new NumberSequenceFormat
        {
            Name = createDto.Name,
            FormatPattern = createDto.FormatPattern,
            CounterDigits = counterDigits,
            ResetsYearly = resetsYearly,
            ResetsMonthly = resetsMonthly,
            IsActive = true
        };

        FormatSet.Add(format);
        await FormatContext.SaveChangesAsync(cancellationToken);

        _logger.LogInformation("Created number sequence format with ID {Id}", format.Id);
        return MapFormatToDto(format);
    }

    /// <summary>
    /// Updates an existing number sequence format.
    /// If the FormatPattern is changed, it must still be valid.
    /// CounterDigits, ResetsYearly, and ResetsMonthly are re-derived from the new pattern.
    /// </summary>
    public async Task<NumberSequenceFormatDto?> UpdateFormatAsync(long formatId, UpdateNumberSequenceFormatDto updateDto, CancellationToken cancellationToken = default)
    {
        _logger.LogInformation("Updating number sequence format {Id}", formatId);

        var format = await FormatSet.FindAsync(new object[] { formatId }, cancellationToken);
        if (format == null)
        {
            _logger.LogWarning("Number sequence format {Id} not found", formatId);
            return null;
        }

        // Apply partial updates — only update fields that are provided
        if (updateDto.Name != null)
            format.Name = updateDto.Name;

        if (updateDto.FormatPattern != null)
        {
            // Validate the new pattern before applying
            if (!ValidateFormatPattern(updateDto.FormatPattern))
                throw new InvalidOperationException($"Invalid format pattern: {updateDto.FormatPattern}");

            format.FormatPattern = updateDto.FormatPattern;

            // Re-derive counter digits and reset behavior from the new pattern
            var (counterDigits, resetsYearly, resetsMonthly) = ParseFormatPattern(updateDto.FormatPattern);
            format.CounterDigits = counterDigits;
            format.ResetsYearly = resetsYearly;
            format.ResetsMonthly = resetsMonthly;
        }

        await FormatContext.SaveChangesAsync(cancellationToken);

        _logger.LogInformation("Number sequence format {Id} updated", formatId);
        return MapFormatToDto(format);
    }

    #endregion

    #region NumberSequence operations

    public async Task<List<NumberSequenceDto>> GetAllSequencesAsync(
        EDocumentType? documentType = null,
        bool includeInactive = false,
        CancellationToken cancellationToken = default)
    {
        // Auto-seed default formats for tenants that were provisioned with empty tables
        await EnsureDefaultFormatsExistAsync(cancellationToken);

        // AsNoTracking: read-only list — results are mapped to DTOs
        var query = _tenantContext.NumberSequence
            .AsNoTracking()
            .Include(s => s.NumberSequenceFormat)
            .AsQueryable();

        if (documentType.HasValue)
            query = query.Where(s => s.DocumentType == documentType.Value);

        if (!includeInactive)
            query = query.Where(s => s.IsActive);

        var sequences = await query
            .OrderBy(s => s.DocumentType)
            .ThenBy(s => s.Name)
            .ToListAsync(cancellationToken);

        return sequences.Select(s => MapSequenceToDto(s)).ToList();
    }

    public async Task<NumberSequenceDto?> GetSequenceByIdAsync(long sequenceId, CancellationToken cancellationToken = default)
    {
        // AsNoTracking: read-only lookup — result is mapped to DTO
        var sequence = await _tenantContext.NumberSequence
            .AsNoTracking()
            .Include(s => s.NumberSequenceFormat)
            .FirstOrDefaultAsync(s => s.Id == sequenceId, cancellationToken);

        return sequence == null ? null : MapSequenceToDto(sequence);
    }

    public async Task<NumberSequenceDto?> GetDefaultSequenceAsync(EDocumentType documentType, CancellationToken cancellationToken = default)
    {
        // AsNoTracking: read-only lookup — result is mapped to DTO
        // OrderBy(Id): deterministic ordering — avoids EF warning when predicate could match multiple rows.
        var sequence = await _tenantContext.NumberSequence
            .AsNoTracking()
            .Include(s => s.NumberSequenceFormat)
            .OrderBy(s => s.Id)
            .FirstOrDefaultAsync(s => s.DocumentType == documentType && s.IsDefault && s.IsActive, cancellationToken);

        return sequence == null ? null : MapSequenceToDto(sequence);
    }

    public async Task<NumberSequenceDto> CreateSequenceAsync(CreateNumberSequenceDto createDto, CancellationToken cancellationToken = default)
    {
        _logger.LogInformation("Creating new number sequence: {Name} for {DocumentType}", createDto.Name, createDto.DocumentType);

        // Auto-seed default formats for tenants that were provisioned with empty tables
        await EnsureDefaultFormatsExistAsync(cancellationToken);

        // Validate format exists — if the requested ID is stale (e.g., after re-provisioning
        // reset the PG sequence and assigned new IDs), fall back to the first active format.
        var format = await _tenantContext.NumberSequenceFormat.FindAsync(new object[] { createDto.NumberSequenceFormatId }, cancellationToken);
        if (format == null)
        {
            // Attempt auto-recovery: use the first active format instead of failing.
            // This handles the case where the UI sent a stale format ID (e.g., after
            // tenant re-provisioning or EnsureDefaultFormatsExistAsync assigned new IDs).
            var fallbackFormat = await _tenantContext.NumberSequenceFormat
                .OrderBy(f => f.Id)
                .FirstOrDefaultAsync(f => f.IsActive, cancellationToken);

            if (fallbackFormat == null)
            {
                throw new InvalidOperationException(
                    $"Number sequence format with ID {createDto.NumberSequenceFormatId} not found " +
                    "and no active formats exist in the database. Please create a format first.");
            }

            _logger.LogWarning(
                "Requested NumberSequenceFormat ID {RequestedId} not found — falling back to ID {FallbackId} ({FallbackName})",
                createDto.NumberSequenceFormatId, fallbackFormat.Id, fallbackFormat.Name);

            format = fallbackFormat;
            createDto.NumberSequenceFormatId = fallbackFormat.Id;
        }

        // If this is set as default, unset other defaults for this document type
        if (createDto.IsDefault)
        {
            var existingDefaults = await _tenantContext.NumberSequence
                .Where(s => s.DocumentType == createDto.DocumentType && s.IsDefault)
                .ToListAsync(cancellationToken);

            foreach (var existing in existingDefaults)
            {
                existing.IsDefault = false;
            }
        }

        var sequence = new NumberSequence
        {
            Name = createDto.Name,
            DocumentType = createDto.DocumentType,
            Prefix = createDto.Prefix,
            Suffix = createDto.Suffix,
            CurrentNumber = createDto.StartingNumber - 1, // Will be incremented on first use
            IsDefault = createDto.IsDefault,
            NumberSequenceFormatId = createDto.NumberSequenceFormatId,
            CurrentYear = null,
            CurrentMonth = null,
            IsActive = true
        };

        _tenantContext.NumberSequence.Add(sequence);
        await _tenantContext.SaveChangesAsync(cancellationToken);

        _logger.LogInformation("Created number sequence with ID {Id}", sequence.Id);

        return (await GetSequenceByIdAsync(sequence.Id, cancellationToken))!;
    }

    /// <summary>
    /// Updates an existing number sequence.
    /// Only Name, Prefix, and Suffix are editable — these are safe fields
    /// that won't break existing numbering sequences.
    /// </summary>
    public async Task<NumberSequenceDto?> UpdateSequenceAsync(long sequenceId, UpdateNumberSequenceDto updateDto, CancellationToken cancellationToken = default)
    {
        _logger.LogInformation("Updating number sequence {Id}", sequenceId);

        var sequence = await _tenantContext.NumberSequence.FindAsync(new object[] { sequenceId }, cancellationToken);
        if (sequence == null)
        {
            _logger.LogWarning("Number sequence {Id} not found", sequenceId);
            return null;
        }

        // Apply partial updates — only update fields that are provided
        if (updateDto.Name != null)
            sequence.Name = updateDto.Name;

        // Prefix and Suffix can be set to empty string (clearing them),
        // so we check for non-null rather than non-empty
        if (updateDto.Prefix != null)
            sequence.Prefix = updateDto.Prefix;

        if (updateDto.Suffix != null)
            sequence.Suffix = updateDto.Suffix;

        await _tenantContext.SaveChangesAsync(cancellationToken);

        _logger.LogInformation("Number sequence {Id} updated", sequenceId);
        return await GetSequenceByIdAsync(sequenceId, cancellationToken);
    }

    public async Task<NumberSequenceDto?> SetAsDefaultAsync(long sequenceId, CancellationToken cancellationToken = default)
    {
        var sequence = await _tenantContext.NumberSequence.FindAsync(new object[] { sequenceId }, cancellationToken);
        if (sequence == null)
            return null;

        _logger.LogInformation("Setting sequence {Id} as default for {DocumentType}", sequenceId, sequence.DocumentType);

        // Unset other defaults for this document type
        var existingDefaults = await _tenantContext.NumberSequence
            .Where(s => s.DocumentType == sequence.DocumentType && s.IsDefault && s.Id != sequenceId)
            .ToListAsync(cancellationToken);

        foreach (var existing in existingDefaults)
        {
            existing.IsDefault = false;
        }

        sequence.IsDefault = true;
        await _tenantContext.SaveChangesAsync(cancellationToken);

        return await GetSequenceByIdAsync(sequenceId, cancellationToken);
    }

    public async Task<bool> DeactivateSequenceAsync(long sequenceId, CancellationToken cancellationToken = default)
    {
        var sequence = await _tenantContext.NumberSequence.FindAsync(new object[] { sequenceId }, cancellationToken);
        if (sequence == null)
            return false;

        if (sequence.IsDefault)
            throw new InvalidOperationException("Cannot deactivate default sequence. Set another sequence as default first.");

        _logger.LogInformation("Deactivating sequence {Id}", sequenceId);

        sequence.IsActive = false;
        await _tenantContext.SaveChangesAsync(cancellationToken);

        return true;
    }

    #endregion

    #region Number generation

    /// <summary>
    /// Maximum number of retry attempts when a concurrency conflict occurs.
    /// A conflict happens when two requests try to generate a number from the same
    /// sequence simultaneously — the SQL Server RowVersion concurrency token prevents duplicate numbers.
    /// </summary>
    private const int MaxConcurrencyRetries = 3;

    public async Task<string> GenerateNextNumberAsync(long sequenceId, DateTime issueDate, CancellationToken cancellationToken = default)
    {
        // Retry loop handles optimistic concurrency conflicts.
        // If another request incremented CurrentNumber between our read and write,
        // EF Core throws DbUpdateConcurrencyException and we retry with fresh data.
        for (int attempt = 0; attempt <= MaxConcurrencyRetries; attempt++)
        {
            var sequence = await _tenantContext.NumberSequence
                .Include(s => s.NumberSequenceFormat)
                .FirstOrDefaultAsync(s => s.Id == sequenceId, cancellationToken);

            if (sequence == null)
                throw new InvalidOperationException($"Number sequence with ID {sequenceId} not found");

            if (!sequence.IsActive)
                throw new InvalidOperationException($"Number sequence {sequence.Name} is not active");

            _logger.LogInformation("Generating next number for sequence {Id} ({Name}), attempt {Attempt}",
                sequenceId, sequence.Name, attempt + 1);

            // Check if we need to reset the counter
            var currentYear = issueDate.Year;
            var currentMonth = issueDate.Month;

            bool needsReset = false;

            if (sequence.NumberSequenceFormat.ResetsYearly && sequence.CurrentYear.HasValue && sequence.CurrentYear.Value != currentYear)
            {
                needsReset = true;
                _logger.LogInformation("Resetting counter for new year: {OldYear} -> {NewYear}", sequence.CurrentYear, currentYear);
            }

            if (sequence.NumberSequenceFormat.ResetsMonthly && sequence.CurrentMonth.HasValue &&
                (sequence.CurrentYear != currentYear || sequence.CurrentMonth.Value != currentMonth))
            {
                needsReset = true;
                _logger.LogInformation("Resetting counter for new month: {OldMonth} -> {NewMonth}", sequence.CurrentMonth, currentMonth);
            }

            if (needsReset)
            {
                sequence.CurrentNumber = 0;
            }

            // Increment counter
            sequence.CurrentNumber++;
            sequence.CurrentYear = currentYear;
            sequence.CurrentMonth = currentMonth;

            // Generate the number
            var number = BuildNumber(sequence, issueDate);

            try
            {
                await _tenantContext.SaveChangesAsync(cancellationToken);

                _logger.LogInformation("Generated number: {Number}", number);
                return number;
            }
            catch (DbUpdateConcurrencyException) when (attempt < MaxConcurrencyRetries)
            {
                // Another request modified this sequence between our read and write.
                // Detach the stale entity so the next iteration loads fresh data.
                _logger.LogWarning(
                    "Concurrency conflict on sequence {Id}, retrying (attempt {Attempt}/{Max})",
                    sequenceId, attempt + 1, MaxConcurrencyRetries);

                _tenantContext.Entry(sequence).State = EntityState.Detached;
            }
        }

        // All retries exhausted — this should be extremely rare
        throw new InvalidOperationException(
            $"Failed to generate document number for sequence {sequenceId} after {MaxConcurrencyRetries} retries due to concurrency conflicts.");
    }

    public async Task<string> GenerateNextNumberForDocumentTypeAsync(
        EDocumentType documentType,
        DateTime issueDate,
        string? prefix = null,
        string? suffix = null,
        CancellationToken cancellationToken = default)
    {
        // OrderBy(Id): deterministic ordering — avoids EF warning when predicate could match multiple rows.
        var sequence = await _tenantContext.NumberSequence
            .Include(s => s.NumberSequenceFormat)
            .OrderBy(s => s.Id)
            .FirstOrDefaultAsync(s => s.DocumentType == documentType && s.IsDefault && s.IsActive, cancellationToken);

        if (sequence == null)
            throw new InvalidOperationException($"No default number sequence configured for {documentType}");

        // Temporarily override prefix/suffix if provided
        var originalPrefix = sequence.Prefix;
        var originalSuffix = sequence.Suffix;

        if (prefix != null)
            sequence.Prefix = prefix;
        if (suffix != null)
            sequence.Suffix = suffix;

        var number = await GenerateNextNumberAsync(sequence.Id, issueDate, cancellationToken);

        // Restore original values (they're not saved since we generated the number already)
        sequence.Prefix = originalPrefix;
        sequence.Suffix = originalSuffix;

        return number;
    }

    public async Task<string> PreviewNextNumberAsync(long sequenceId, DateTime issueDate, CancellationToken cancellationToken = default)
    {
        var sequence = await _tenantContext.NumberSequence
            .Include(s => s.NumberSequenceFormat)
            .AsNoTracking() // Don't track changes
            .FirstOrDefaultAsync(s => s.Id == sequenceId, cancellationToken);

        if (sequence == null)
            throw new InvalidOperationException($"Number sequence with ID {sequenceId} not found");

        // Simulate what would happen
        var currentYear = issueDate.Year;
        var currentMonth = issueDate.Month;

        bool needsReset = false;

        if (sequence.NumberSequenceFormat.ResetsYearly && sequence.CurrentYear.HasValue && sequence.CurrentYear.Value != currentYear)
            needsReset = true;

        if (sequence.NumberSequenceFormat.ResetsMonthly && sequence.CurrentMonth.HasValue &&
            (sequence.CurrentYear != currentYear || sequence.CurrentMonth.Value != currentMonth))
            needsReset = true;

        if (needsReset)
            sequence.CurrentNumber = 0;

        sequence.CurrentNumber++;

        return BuildNumber(sequence, issueDate);
    }

    /// <summary>
    /// Previews the next document number for a given document type using the default sequence.
    /// Returns null if no default sequence is configured — useful for UI hints where
    /// a missing sequence is not an error (the user just won't see a preview).
    /// </summary>
    public async Task<string?> PreviewNextNumberForDocumentTypeAsync(
        EDocumentType documentType, DateTime issueDate, CancellationToken cancellationToken = default)
    {
        // Find the default sequence for this document type.
        // OrderBy(Id): deterministic ordering — avoids EF warning when predicate could match multiple rows.
        var defaultSequence = await _tenantContext.NumberSequence
            .Include(s => s.NumberSequenceFormat)
            .AsNoTracking()
            .OrderBy(s => s.Id)
            .FirstOrDefaultAsync(s => s.DocumentType == documentType && s.IsDefault && s.IsActive, cancellationToken);

        if (defaultSequence == null)
        {
            _logger.LogDebug("No default sequence for {DocumentType} — cannot preview", documentType);
            return null;
        }

        // Delegate to the existing preview method
        return await PreviewNextNumberAsync(defaultSequence.Id, issueDate, cancellationToken);
    }

    /// <inheritdoc />
    public async Task<bool> TryReleaseLastNumberAsync(
        EDocumentType documentType,
        int expectedCurrentNumber,
        CancellationToken cancellationToken = default)
    {
        // Find the default sequence for this document type.
        var sequence = await _tenantContext.NumberSequence
            .OrderBy(s => s.Id)
            .FirstOrDefaultAsync(
                s => s.DocumentType == documentType && s.IsDefault && s.IsActive,
                cancellationToken);

        if (sequence == null)
        {
            _logger.LogDebug("No default sequence for {DocumentType} — cannot release number", documentType);
            return false;
        }

        // Only decrement if the counter is exactly what we expect.
        // This means the deleted invoice had the LAST generated number.
        // If another invoice was created after it, CurrentNumber will be higher
        // and we cannot release (would create a gap).
        if (sequence.CurrentNumber != expectedCurrentNumber)
        {
            _logger.LogDebug(
                "Cannot release number for {DocumentType}: expected counter={Expected}, actual={Actual}. " +
                "Another document was generated after the deleted one.",
                documentType, expectedCurrentNumber, sequence.CurrentNumber);
            return false;
        }

        // Decrement — the deleted invoice's number will be reused by the next invoice.
        sequence.CurrentNumber--;

        _logger.LogInformation(
            "Released last number for {DocumentType} sequence '{Name}': counter decremented from {From} to {To}",
            documentType, sequence.Name, expectedCurrentNumber, sequence.CurrentNumber);

        try
        {
            await _tenantContext.SaveChangesAsync(cancellationToken);
            return true;
        }
        catch (DbUpdateConcurrencyException)
        {
            // Another request modified the sequence concurrently — cannot safely release.
            _logger.LogWarning(
                "Concurrency conflict releasing number for {DocumentType} — number NOT released",
                documentType);
            return false;
        }
    }

    #endregion

    #region Format validation

    public bool ValidateFormatPattern(string formatPattern)
    {
        if (string.IsNullOrWhiteSpace(formatPattern))
            return false;

        // Pattern must contain at least one N (for counter)
        if (!formatPattern.Contains('N'))
            return false;

        // Valid characters: y (year), M (month), N (counter), - (separator), / (separator)
        var validChars = new HashSet<char> { 'y', 'M', 'N', '-', '/' };
        if (!formatPattern.All(c => validChars.Contains(c)))
            return false;

        // yyyy or yy for year (if present)
        if (formatPattern.Contains('y'))
        {
            var yearMatch = Regex.Match(formatPattern, @"y+");
            if (yearMatch.Success)
            {
                var yearLength = yearMatch.Value.Length;
                if (yearLength != 2 && yearLength != 4)
                    return false;
            }
        }

        // MM for month (if present)
        if (formatPattern.Contains('M'))
        {
            var monthMatch = Regex.Match(formatPattern, @"M+");
            if (monthMatch.Success && monthMatch.Value.Length != 2)
                return false;
        }

        return true;
    }

    public string GetFormatPatternDescription(string formatPattern)
    {
        if (!ValidateFormatPattern(formatPattern))
            return "Invalid format pattern";

        var description = formatPattern
            .Replace("yyyy", "[4-digit year]")
            .Replace("yy", "[2-digit year]")
            .Replace("MM", "[2-digit month]");

        // Count N characters
        var counterDigits = formatPattern.Count(c => c == 'N');
        description = description.Replace(new string('N', counterDigits), $"[{counterDigits}-digit counter]");

        var hasYear = formatPattern.Contains('y');
        var hasMonth = formatPattern.Contains('M');

        var resetInfo = "";
        if (hasMonth)
            resetInfo = " (resets monthly)";
        else if (hasYear)
            resetInfo = " (resets yearly)";
        else
            resetInfo = " (continuous)";

        return description + resetInfo;
    }

    #endregion

    #region Private helpers

    /// <summary>
    /// Parses format pattern to extract counter digits and reset behavior
    /// </summary>
    private (int counterDigits, bool resetsYearly, bool resetsMonthly) ParseFormatPattern(string formatPattern)
    {
        var counterDigits = formatPattern.Count(c => c == 'N');
        var resetsYearly = formatPattern.Contains('y');
        var resetsMonthly = formatPattern.Contains('M');

        return (counterDigits, resetsYearly, resetsMonthly);
    }

    /// <summary>
    /// Builds the actual number string from sequence and date
    /// </summary>
    private string BuildNumber(NumberSequence sequence, DateTime issueDate)
    {
        var pattern = sequence.NumberSequenceFormat.FormatPattern;
        var result = pattern;

        // Replace year
        if (pattern.Contains("yyyy"))
            result = result.Replace("yyyy", issueDate.Year.ToString("0000"));
        else if (pattern.Contains("yy"))
            result = result.Replace("yy", (issueDate.Year % 100).ToString("00"));

        // Replace month
        if (pattern.Contains("MM"))
            result = result.Replace("MM", issueDate.Month.ToString("00"));

        // Replace counter
        var counterDigits = sequence.NumberSequenceFormat.CounterDigits;
        var counterFormat = new string('0', counterDigits);
        var counterPlaceholder = new string('N', counterDigits);
        result = result.Replace(counterPlaceholder, sequence.CurrentNumber.ToString(counterFormat));

        // Add prefix and suffix
        if (!string.IsNullOrEmpty(sequence.Prefix))
            result = sequence.Prefix + result;

        if (!string.IsNullOrEmpty(sequence.Suffix))
            result = result + sequence.Suffix;

        return result;
    }


    #endregion
}

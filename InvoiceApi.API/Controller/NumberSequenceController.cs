using InvoiceApi.Application.Dto.NumberSequence;
using InvoiceApi.Application.Service;
using InvoiceApi.Domain.Enums;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace InvoiceApi.API.Controller;

/// <summary>
/// Controller for managing number sequences and formats
/// Handles number generation configuration for invoices and credit notes
/// All users can read, only Admin/SysAdmin can modify
/// </summary>
[ApiController]
[Route("api/[controller]")]
[Produces("application/json")]
[Authorize] // All endpoints require authentication
public class NumberSequenceController : ControllerBase
{
    private readonly INumberSequenceService _numberSequenceService;
    private readonly ILogger<NumberSequenceController> _logger;

    public NumberSequenceController(
        INumberSequenceService numberSequenceService,
        ILogger<NumberSequenceController> logger)
    {
        _numberSequenceService = numberSequenceService;
        _logger = logger;
    }

    #region Format endpoints

    /// <summary>
    /// Gets all number sequence formats
    /// </summary>
    /// <param name="includeInactive">Include inactive formats</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>List of formats</returns>
    /// <response code="200">Returns list of formats</response>
    [HttpGet("formats")]
    [ProducesResponseType(typeof(List<NumberSequenceFormatDto>), StatusCodes.Status200OK)]
    public async Task<ActionResult<List<NumberSequenceFormatDto>>> GetAllFormats(
        [FromQuery] bool includeInactive = false,
        CancellationToken cancellationToken = default)
    {
        _logger.LogInformation("GET /api/numbersequence/formats - includeInactive: {IncludeInactive}", includeInactive);
        var formats = await _numberSequenceService.GetAllFormatsAsync(includeInactive, cancellationToken);
        return Ok(formats);
    }

    /// <summary>
    /// Gets a specific format by ID
    /// </summary>
    /// <param name="id">Format ID</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>Format data</returns>
    /// <response code="200">Returns the format</response>
    /// <response code="404">Format not found</response>
    [HttpGet("formats/{id}")]
    [ProducesResponseType(typeof(NumberSequenceFormatDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<NumberSequenceFormatDto>> GetFormatById(
        long id,
        CancellationToken cancellationToken = default)
    {
        _logger.LogInformation("GET /api/numbersequence/formats/{Id}", id);

        var format = await _numberSequenceService.GetFormatByIdAsync(id, cancellationToken);

        if (format == null)
        {
            _logger.LogWarning("Format {Id} not found", id);
            return NotFound(new { message = $"Format with ID {id} not found" });
        }

        return Ok(format);
    }

    /// <summary>
    /// Creates a new number sequence format
    /// </summary>
    /// <param name="createDto">Format data</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>Created format</returns>
    /// <response code="201">Format created successfully</response>
    /// <response code="400">Invalid format pattern</response>
    [HttpPost("formats")]
    [Authorize(Roles = "Admin,SysAdmin")] // Only Admin and SysAdmin can create formats
    [ProducesResponseType(typeof(NumberSequenceFormatDto), StatusCodes.Status201Created)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public async Task<ActionResult<NumberSequenceFormatDto>> CreateFormat(
        [FromBody] CreateNumberSequenceFormatDto createDto,
        CancellationToken cancellationToken = default)
    {
        _logger.LogInformation("POST /api/numbersequence/formats - Creating format {Name}", createDto.Name);

        try
        {
            var format = await _numberSequenceService.CreateFormatAsync(createDto, cancellationToken);

            _logger.LogInformation("Format created with ID {Id}", format.Id);

            return CreatedAtAction(
                nameof(GetFormatById),
                new { id = format.Id },
                format);
        }
        catch (InvalidOperationException ex)
        {
            _logger.LogWarning("Failed to create format: {Message}", ex.Message);
            return BadRequest(new { message = ex.Message });
        }
    }

    /// <summary>
    /// Updates an existing number sequence format.
    /// Only Name and FormatPattern are editable.
    /// </summary>
    /// <param name="id">Format ID</param>
    /// <param name="updateDto">Fields to update</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>Updated format</returns>
    /// <response code="200">Format updated successfully</response>
    /// <response code="400">Invalid format pattern</response>
    /// <response code="404">Format not found</response>
    [HttpPut("formats/{id}")]
    [Authorize(Roles = "Admin,SysAdmin")]
    [ProducesResponseType(typeof(NumberSequenceFormatDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<NumberSequenceFormatDto>> UpdateFormat(
        long id,
        [FromBody] UpdateNumberSequenceFormatDto updateDto,
        CancellationToken cancellationToken = default)
    {
        _logger.LogInformation("PUT /api/numbersequence/formats/{Id}", id);

        try
        {
            var format = await _numberSequenceService.UpdateFormatAsync(id, updateDto, cancellationToken);
            if (format == null)
            {
                _logger.LogWarning("Format {Id} not found for update", id);
                return NotFound(new { message = $"Format with ID {id} not found" });
            }

            _logger.LogInformation("Format {Id} updated successfully", id);
            return Ok(format);
        }
        catch (InvalidOperationException ex)
        {
            _logger.LogWarning("Failed to update format {Id}: {Message}", id, ex.Message);
            return BadRequest(new { message = ex.Message });
        }
    }

    /// <summary>
    /// Validates a format pattern
    /// </summary>
    /// <param name="pattern">Pattern to validate (e.g., "yyyyNNN", "yyMMNNN")</param>
    /// <returns>Validation result with description</returns>
    /// <response code="200">Returns validation result</response>
    [HttpGet("formats/validate")]
    [ProducesResponseType(typeof(object), StatusCodes.Status200OK)]
    public ActionResult ValidateFormatPattern([FromQuery] string pattern)
    {
        _logger.LogInformation("GET /api/numbersequence/formats/validate - pattern: {Pattern}", pattern);

        var isValid = _numberSequenceService.ValidateFormatPattern(pattern);
        var description = _numberSequenceService.GetFormatPatternDescription(pattern);

        return Ok(new
        {
            pattern,
            isValid,
            description,
            example = isValid ? GetPatternExample(pattern) : null
        });
    }

    #endregion

    #region Sequence endpoints

    /// <summary>
    /// Gets all number sequences
    /// </summary>
    /// <param name="documentType">Filter by document type</param>
    /// <param name="includeInactive">Include inactive sequences</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>List of sequences</returns>
    /// <response code="200">Returns list of sequences</response>
    [HttpGet]
    [ProducesResponseType(typeof(List<NumberSequenceDto>), StatusCodes.Status200OK)]
    public async Task<ActionResult<List<NumberSequenceDto>>> GetAllSequences(
        [FromQuery] EDocumentType? documentType = null,
        [FromQuery] bool includeInactive = false,
        CancellationToken cancellationToken = default)
    {
        _logger.LogInformation("GET /api/numbersequence - documentType: {DocumentType}, includeInactive: {IncludeInactive}",
            documentType, includeInactive);

        var sequences = await _numberSequenceService.GetAllSequencesAsync(documentType, includeInactive, cancellationToken);
        return Ok(sequences);
    }

    /// <summary>
    /// Gets a specific sequence by ID
    /// </summary>
    /// <param name="id">Sequence ID</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>Sequence data</returns>
    /// <response code="200">Returns the sequence</response>
    /// <response code="404">Sequence not found</response>
    [HttpGet("{id}")]
    [ProducesResponseType(typeof(NumberSequenceDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<NumberSequenceDto>> GetSequenceById(
        long id,
        CancellationToken cancellationToken = default)
    {
        _logger.LogInformation("GET /api/numbersequence/{Id}", id);

        var sequence = await _numberSequenceService.GetSequenceByIdAsync(id, cancellationToken);

        if (sequence == null)
        {
            _logger.LogWarning("Sequence {Id} not found", id);
            return NotFound(new { message = $"Sequence with ID {id} not found" });
        }

        return Ok(sequence);
    }

    /// <summary>
    /// Gets the default sequence for a document type
    /// </summary>
    /// <param name="documentType">Document type (Invoice or CreditNote)</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>Default sequence</returns>
    /// <response code="200">Returns the default sequence</response>
    /// <response code="404">No default sequence configured</response>
    [HttpGet("default/{documentType}")]
    [ProducesResponseType(typeof(NumberSequenceDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<NumberSequenceDto>> GetDefaultSequence(
        EDocumentType documentType,
        CancellationToken cancellationToken = default)
    {
        _logger.LogInformation("GET /api/numbersequence/default/{DocumentType}", documentType);

        var sequence = await _numberSequenceService.GetDefaultSequenceAsync(documentType, cancellationToken);

        if (sequence == null)
        {
            _logger.LogWarning("No default sequence configured for {DocumentType}", documentType);
            return NotFound(new { message = $"No default sequence configured for {documentType}" });
        }

        return Ok(sequence);
    }

    /// <summary>
    /// Creates a new number sequence
    /// </summary>
    /// <param name="createDto">Sequence data</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>Created sequence</returns>
    /// <response code="201">Sequence created successfully</response>
    /// <response code="400">Invalid request data</response>
    [HttpPost]
    [Authorize(Roles = "Admin,SysAdmin")] // Only Admin and SysAdmin can create sequences
    [ProducesResponseType(typeof(NumberSequenceDto), StatusCodes.Status201Created)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public async Task<ActionResult<NumberSequenceDto>> CreateSequence(
        [FromBody] CreateNumberSequenceDto createDto,
        CancellationToken cancellationToken = default)
    {
        _logger.LogInformation("POST /api/numbersequence - Creating sequence {Name} for {DocumentType}",
            createDto.Name, createDto.DocumentType);

        try
        {
            var sequence = await _numberSequenceService.CreateSequenceAsync(createDto, cancellationToken);

            _logger.LogInformation("Sequence created with ID {Id}", sequence.Id);

            return CreatedAtAction(
                nameof(GetSequenceById),
                new { id = sequence.Id },
                sequence);
        }
        catch (InvalidOperationException ex)
        {
            _logger.LogWarning("Failed to create sequence: {Message}", ex.Message);
            return BadRequest(new { message = ex.Message });
        }
    }

    /// <summary>
    /// Updates an existing number sequence.
    /// Only Name, Prefix, and Suffix are editable (safe fields).
    /// </summary>
    /// <param name="id">Sequence ID</param>
    /// <param name="updateDto">Fields to update</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>Updated sequence</returns>
    /// <response code="200">Sequence updated successfully</response>
    /// <response code="404">Sequence not found</response>
    [HttpPut("{id}")]
    [Authorize(Roles = "Admin,SysAdmin")]
    [ProducesResponseType(typeof(NumberSequenceDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<NumberSequenceDto>> UpdateSequence(
        long id,
        [FromBody] UpdateNumberSequenceDto updateDto,
        CancellationToken cancellationToken = default)
    {
        _logger.LogInformation("PUT /api/numbersequence/{Id}", id);

        var sequence = await _numberSequenceService.UpdateSequenceAsync(id, updateDto, cancellationToken);
        if (sequence == null)
        {
            _logger.LogWarning("Sequence {Id} not found for update", id);
            return NotFound(new { message = $"Sequence with ID {id} not found" });
        }

        _logger.LogInformation("Sequence {Id} updated successfully", id);
        return Ok(sequence);
    }

    /// <summary>
    /// Sets a sequence as default for its document type
    /// </summary>
    /// <param name="id">Sequence ID</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>Updated sequence</returns>
    /// <response code="200">Sequence set as default successfully</response>
    /// <response code="404">Sequence not found</response>
    [HttpPost("{id}/set-default")]
    [Authorize(Roles = "Admin,SysAdmin")] // Only Admin and SysAdmin can set default sequences
    [ProducesResponseType(typeof(NumberSequenceDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<NumberSequenceDto>> SetAsDefault(
        long id,
        CancellationToken cancellationToken = default)
    {
        _logger.LogInformation("POST /api/numbersequence/{Id}/set-default", id);

        var sequence = await _numberSequenceService.SetAsDefaultAsync(id, cancellationToken);

        if (sequence == null)
        {
            _logger.LogWarning("Sequence {Id} not found", id);
            return NotFound(new { message = $"Sequence with ID {id} not found" });
        }

        _logger.LogInformation("Sequence {Id} set as default", id);
        return Ok(sequence);
    }

    /// <summary>
    /// Deactivates a number sequence
    /// Cannot deactivate if it's the default sequence
    /// </summary>
    /// <param name="id">Sequence ID</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>No content</returns>
    /// <response code="204">Sequence deactivated successfully</response>
    /// <response code="404">Sequence not found</response>
    /// <response code="400">Cannot deactivate default sequence</response>
    [HttpDelete("{id}")]
    [Authorize(Roles = "Admin,SysAdmin")] // Only Admin and SysAdmin can delete sequences
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public async Task<IActionResult> DeactivateSequence(
        long id,
        CancellationToken cancellationToken = default)
    {
        _logger.LogInformation("DELETE /api/numbersequence/{Id}", id);

        try
        {
            var deactivated = await _numberSequenceService.DeactivateSequenceAsync(id, cancellationToken);

            if (!deactivated)
            {
                _logger.LogWarning("Sequence {Id} not found for deactivation", id);
                return NotFound(new { message = $"Sequence with ID {id} not found" });
            }

            _logger.LogInformation("Sequence {Id} deactivated successfully", id);
            return NoContent();
        }
        catch (InvalidOperationException ex)
        {
            _logger.LogWarning("Cannot deactivate sequence {Id}: {Message}", id, ex.Message);
            return BadRequest(new { message = ex.Message });
        }
    }

    /// <summary>
    /// Previews what the next number would be for a sequence
    /// Does not increment the counter
    /// </summary>
    /// <param name="id">Sequence ID</param>
    /// <param name="issueDate">Issue date (optional, defaults to today)</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>Preview of next number</returns>
    /// <response code="200">Returns preview</response>
    /// <response code="404">Sequence not found</response>
    /// <response code="400">Invalid request</response>
    [HttpGet("{id}/preview")]
    [ProducesResponseType(typeof(object), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public async Task<ActionResult> PreviewNextNumber(
        long id,
        [FromQuery] DateTime? issueDate = null,
        CancellationToken cancellationToken = default)
    {
        var date = issueDate ?? DateTime.UtcNow;
        _logger.LogInformation("GET /api/numbersequence/{Id}/preview - issueDate: {IssueDate}", id, date);

        try
        {
            var preview = await _numberSequenceService.PreviewNextNumberAsync(id, date, cancellationToken);

            return Ok(new
            {
                sequenceId = id,
                issueDate = date,
                nextNumber = preview
            });
        }
        catch (InvalidOperationException ex)
        {
            _logger.LogWarning("Failed to preview number for sequence {Id}: {Message}", id, ex.Message);
            return BadRequest(new { message = ex.Message });
        }
    }

    /// <summary>
    /// Previews the next document number for a given document type using the default sequence.
    /// Does not increment the counter — useful for showing the user what number will be assigned.
    /// Returns 404 if no default sequence is configured for the document type.
    /// </summary>
    /// <param name="documentType">Document type (Invoice or CreditNote)</param>
    /// <param name="issueDate">Optional issue date (defaults to today)</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>Preview of next number</returns>
    /// <response code="200">Returns preview</response>
    /// <response code="404">No default sequence configured for this document type</response>
    [HttpGet("preview-by-type")]
    [ProducesResponseType(typeof(object), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult> PreviewByDocumentType(
        [FromQuery] EDocumentType documentType,
        [FromQuery] DateTime? issueDate = null,
        CancellationToken cancellationToken = default)
    {
        var date = issueDate ?? DateTime.UtcNow;
        _logger.LogInformation("GET /api/numbersequence/preview-by-type - documentType: {DocumentType}, issueDate: {IssueDate}",
            documentType, date);

        var preview = await _numberSequenceService.PreviewNextNumberForDocumentTypeAsync(documentType, date, cancellationToken);

        if (preview == null)
        {
            return NotFound(new { message = $"No default sequence configured for {documentType}" });
        }

        return Ok(new
        {
            documentType,
            issueDate = date,
            nextNumber = preview
        });
    }

    #endregion

    #region Private helpers

    /// <summary>
    /// Generates an example number for a format pattern
    /// </summary>
    private string GetPatternExample(string pattern)
    {
        var now = DateTime.Now;
        var result = pattern;

        // Replace year
        if (pattern.Contains("yyyy"))
            result = result.Replace("yyyy", now.Year.ToString("0000"));
        else if (pattern.Contains("yy"))
            result = result.Replace("yy", (now.Year % 100).ToString("00"));

        // Replace month
        if (pattern.Contains("MM"))
            result = result.Replace("MM", now.Month.ToString("00"));

        // Replace counter with example (always use 1)
        var counterDigits = pattern.Count(c => c == 'N');
        var counterFormat = new string('0', counterDigits);
        var counterPlaceholder = new string('N', counterDigits);
        result = result.Replace(counterPlaceholder, 1.ToString(counterFormat));

        return result;
    }

    #endregion
}

using System.ComponentModel.DataAnnotations;
using Fakvio.Contracts.Dto.Client;

namespace Fakvio.Contracts.Dto.Import;

/// <summary>
/// Outcome of matching a single CSV row against the existing client database, before import.
/// </summary>
public enum EClientImportRowStatus
{
    /// <summary>No matching client found — will be created on confirm.</summary>
    New = 1,

    /// <summary>A client with the same IČO already exists (either in the database, or earlier
    /// in the same CSV file) — will be skipped on confirm.</summary>
    Duplicate = 2,

    /// <summary>The row is missing required data (e.g. company name) and cannot be imported.</summary>
    Invalid = 3
}

/// <summary>
/// One row of a client CSV import preview.
/// The user reviews this (and the <see cref="Status"/>/<see cref="Reason"/>) before confirming.
/// </summary>
public class ClientImportPreviewRowDto
{
    /// <summary>1-based row number in the CSV file (header row is row 0), for error messages.</summary>
    public int RowNumber { get; set; }

    /// <summary>Client data mapped from the CSV row, ready to send to <c>CreateClientAsync</c> if <see cref="Status"/> is New.</summary>
    public CreateClientDto Client { get; set; } = new();

    /// <summary>New / Duplicate / Invalid — see enum docs.</summary>
    public EClientImportRowStatus Status { get; set; }

    /// <summary>Human-readable explanation for Duplicate/Invalid rows (e.g. "IČO already exists").</summary>
    public string? Reason { get; set; }

    /// <summary>ID of the existing client this row duplicates, if <see cref="Status"/> is Duplicate and the match is in the database.</summary>
    public long? ExistingClientId { get; set; }
}

/// <summary>
/// Full preview result for a client CSV import. Nothing is saved at this stage — the user
/// reviews the rows and then calls the confirm endpoint with the rows they want to keep.
/// </summary>
public class ClientImportPreviewDto
{
    public List<ClientImportPreviewRowDto> Rows { get; set; } = new();

    /// <summary>Column headers found in the file that didn't match any known alias (ignored, shown for transparency).</summary>
    public List<string> UnknownColumns { get; set; } = new();

    public int NewCount => Rows.Count(r => r.Status == EClientImportRowStatus.New);
    public int DuplicateCount => Rows.Count(r => r.Status == EClientImportRowStatus.Duplicate);
    public int InvalidCount => Rows.Count(r => r.Status == EClientImportRowStatus.Invalid);
}

/// <summary>
/// Request to confirm a client CSV import — the user-reviewed subset of rows to actually create.
/// Typically this is the preview's New rows, re-sent as-is (or lightly edited by the user).
/// </summary>
public class ClientImportConfirmDto
{
    [Required, MinLength(1, ErrorMessage = "At least one client must be provided.")]
    public List<CreateClientDto> Clients { get; set; } = new();
}

/// <summary>
/// Result of a confirmed client CSV import.
/// </summary>
public class ClientImportResultDto
{
    /// <summary>How many clients were actually created.</summary>
    public int CreatedCount { get; set; }

    /// <summary>How many were skipped because a duplicate (by IČO) was found at confirm time.</summary>
    public int SkippedCount { get; set; }

    /// <summary>IDs of the newly created clients, in the same order as the request.</summary>
    public List<long> CreatedClientIds { get; set; } = new();

    /// <summary>One message per row that failed to import (validation error from the service layer, etc.).</summary>
    public List<string> Errors { get; set; } = new();
}

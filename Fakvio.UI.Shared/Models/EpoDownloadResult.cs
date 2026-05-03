namespace Fakvio.UI.Shared.Models;

/// <summary>
/// Result of an EPO XML download attempt.
/// On success, FileBytes and FileName are set.
/// On error, ErrorCode and MissingFields describe the problem.
/// This avoids exceptions crossing the service boundary for known business errors.
/// </summary>
public class EpoDownloadResult
{
    /// <summary>
    /// True when the API returned 200 with the XML file.
    /// </summary>
    public bool IsSuccess { get; init; }

    /// <summary>
    /// Raw XML bytes returned by the API (populated on success).
    /// </summary>
    public byte[]? FileBytes { get; init; }

    /// <summary>
    /// Suggested file name extracted from the Content-Disposition header (populated on success).
    /// Falls back to a generated name when the header is absent.
    /// </summary>
    public string? FileName { get; init; }

    /// <summary>
    /// Error code from the API JSON body, e.g. "EPO_HEADER_INCOMPLETE" or "VAT_PAYER_REQUIRED".
    /// Populated on failure.
    /// </summary>
    public string? ErrorCode { get; init; }

    /// <summary>
    /// Human-readable error message from the API.
    /// Populated on failure.
    /// </summary>
    public string? ErrorMessage { get; init; }

    /// <summary>
    /// List of missing EPO header fields returned by the API for EPO_HEADER_INCOMPLETE errors.
    /// E.g. ["EpoTaxOfficeCode", "EpoContactEmail"]. Populated on failure with that code.
    /// </summary>
    public List<string> MissingFields { get; init; } = [];

    // ── Factory helpers ──────────────────────────────────────────────────────

    /// <summary>Creates a success result.</summary>
    public static EpoDownloadResult Success(byte[] bytes, string fileName)
        => new() { IsSuccess = true, FileBytes = bytes, FileName = fileName };

    /// <summary>Creates a failure result.</summary>
    public static EpoDownloadResult Failure(string errorCode, string errorMessage, List<string>? missingFields = null)
        => new()
        {
            IsSuccess = false,
            ErrorCode = errorCode,
            ErrorMessage = errorMessage,
            MissingFields = missingFields ?? []
        };
}
